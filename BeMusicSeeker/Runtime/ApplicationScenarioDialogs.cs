#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Runtime;

/// <summary>明示sessionでarmした既知の一入力だけを消費します。その他のdialogは通常の所有者付き表示へ委譲します。</summary>
internal sealed partial class ApplicationScenarioDialogs : IUiDialogService
{
    private readonly IUiDialogService inner = new UiDialogCoordinator();
    private object? pending;

    /// <summary>次の実property画面へ名前・シンボルまたは上下操作を一回渡します。</summary>
    internal void ArmProperty(string? name, string? symbol, string? moveFolder = null, bool moveDown = false)
        => Arm(new PropertyInput(name, symbol, moveFolder, moveDown));
    /// <summary>次の実設定画面へ即時fontと未確定path候補を一回渡します。</summary>
    internal Task<ScenarioSettingsEditObservation> ArmSettings(double fontSize, string candidatePath)
    {
        var input = new SettingsInput(fontSize, candidatePath);
        Arm(input);
        return input.Completion.Task;
    }
    /// <summary>次のJSON二path選択にだけ、宣言済み固定pathを返します。</summary>
    internal void ArmJson(string headerPath, string dataPath) => Arm(new PlaylistJsonExportPaths(headerPath, dataPath));
    /// <summary>入力を消費したことを確認します。未表示を成功へ置き換えません。</summary>
    internal void AssertConsumed()
    {
        if (pending is not null) { throw new InvalidOperationException("The declared dialog input was not consumed."); }
    }
    private void Arm(object input)
    {
        if (pending is not null) { throw new InvalidOperationException("A dialog input is already armed."); }
        pending = input;
    }

    /// <inheritdoc />
    public async Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default) where TWindow : Window
    {
        if (typeof(TWindow) == typeof(PlaylistJsonExportDialog) && typeof(TResult) == typeof(PlaylistJsonExportPaths)
            && pending is PlaylistJsonExportPaths paths)
        {
            pending = null;
            return new(UiDialogStatus.Accepted, (TResult)(object)paths, true);
        }
        object? input = (typeof(TWindow) == typeof(PlaylistPropertyDialog) && pending is PropertyInput)
            || (typeof(TWindow) == typeof(SettingsWindow) && pending is SettingsInput or SqlInput) ? pending : null;
        if (input is null) { return await inner.ShowWindowAsync(request, cancellationToken); }
        pending = null;
        Exception? inputFailure = null;
        Task inputTask = Task.CompletedTask;
        UiWindowDialogResult<TResult> result = await inner.ShowWindowAsync(new UiWindowDialogRequest<TWindow, TResult>(() =>
        {
            TWindow created = request.CreateWindow();
            created.ContentRendered += Rendered;
            return created;
            async void Rendered(object? sender, EventArgs args)
            {
                created.ContentRendered -= Rendered;
                inputTask = ApplyAfterPresentationAsync(created, input);
                try { await inputTask; }
                catch (Exception failure)
                {
                    inputFailure = failure;
                    if (created is PlaylistPropertyDialog property) { property.CloseForOwnerShutdown(); }
                    else { created.Close(); }
                }
            }
        }, request.CreateResult, request.Owner), cancellationToken);
        // SQLの実handlerはmodalを閉じた後にもUI継続を持ちます。同期のShowSettingsWindowへ先に帰還し、
        // 同じhandlerの終端・入力消費・元例外はExecuteAsyncが保持するSqlInput.Completionだけで回収します。
        if (input is SqlInput) { return result; }
        // modal帰還後の呼出元は同期境界なので、入力の実終端回収だけをUIへ再配送しません。
        await inputTask.ConfigureAwait(false);
        if (inputFailure is not null) { throw new InvalidOperationException("The declared dialog input failed.", inputFailure); }
        return result;
    }

    private async Task ApplyAfterPresentationAsync(Window window, object input)
    {
        // SettingsWindowはbaseのContentRendered通知後にpresentationを有効化するため、そのoverrideの帰還後に入力します。
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        // 入力はUI上で開始し、閉鎖後の空の継続はmodal帰還先の同期待ちへ依存させません。
        await ApplyInputAsync(window, input).ConfigureAwait(false);
    }

    private async Task ApplyInputAsync(Window window, object input)
    {
        if (window is SettingsWindow sqlWindow && input is SqlInput sql)
        {
            try
            {
                activeSql = sql;
                if (sql.Restore) { await sqlWindow.HandlePlaylistRestoreAsync(); }
                else { await sqlWindow.HandlePlaylistBackupAsync(); }
                if (!sql.PathConsumed || (sql.Restore && (!sql.ConfirmationConsumed || !sql.RestoreSuccessConsumed || !sql.ExitNoticeConsumed)))
                { throw new InvalidOperationException("The declared SQL dialog sequence did not complete."); }
                if (!sql.Restore) { sqlWindow.Close(); }
                sql.Completion.SetResult();
            }
            catch (Exception failure) { sql.Completion.TrySetException(failure); throw; }
            finally { activeSql = null; }
            return;
        }
        if (window is PlaylistPropertyDialog property && input is PropertyInput values
            && property.DataContext is PlaylistPropertyDialogViewModel model)
        {
            if (values.Name is not null) { model.name = values.Name; }
            if (values.Symbol is not null) { model.symbol = values.Symbol; }
            if (values.MoveFolder is not null)
            {
                model.is_auto_folder_sort = false;
                ((ListBox)property.FindName("propertyNavigation")).SelectedIndex = 1;
                property.UpdateLayout();
                ((ListBox)property.FindName("foldersListBox")).SelectedItem = values.MoveFolder;
                Click(property, values.MoveDown ? "PlaylistPropertyFolderMoveDown" : "PlaylistPropertyFolderMoveUp");
            }
            Click(property, "PlaylistPropertyAccept");
            await property.WaitForOperationCompletionAsync();
            if (!property.HasTerminalOutcome) { throw new InvalidOperationException("The property save did not complete and close."); }
            return;
        }
        if (window is SettingsWindow && input is SettingsInput settings && window.DataContext is SettingsDialogViewModel settingsModel)
        {
            settingsModel.CustomTableFontSize = settings.FontSize;
            var fresh = new Settings();
            fresh.Reload();
            var raw = XDocument.Load(PortableSettingsPath.UserConfigPath);
            string Read(string name) => raw.Descendants("setting").Single(element => (string?)element.Attribute("name") == name)
                .Element("value")?.Value ?? throw new InvalidOperationException("The committed setting is absent: " + name);
            double savedFont = fresh.CustomTableFontSize;
            string savedPath = fresh.BeatorajaRootPath;
            string rawFont = Read("CustomTableFontSize");
            string rawPath = Read("BeatorajaRootPath");
            settingsModel.BeatorajaRootPath = settings.CandidatePath;
            string candidateBeforeClose = settingsModel.BeatorajaRootPath;
            settingsModel.CloseCommand.Execute();
            settings.Completion.SetResult(new(savedFont, savedPath, rawFont, rawPath,
                candidateBeforeClose, settingsModel.BeatorajaRootPath));
            return;
        }
        throw new InvalidOperationException("The declared input does not match the real dialog session.");
    }

    private static void Click(DependencyObject root, string id)
    {
        Button button = Descendants(root).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == id);
        if (!button.IsEnabled) { throw new InvalidOperationException("The requested property control is disabled: " + id); }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) { yield return nested; }
        }
    }
    private sealed record PropertyInput(string? Name, string? Symbol, string? MoveFolder, bool MoveDown);
    private sealed record SettingsInput(double FontSize, string CandidatePath)
    {
        internal TaskCompletionSource<ScenarioSettingsEditObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    /// <inheritdoc />
    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        => ShowRecoveryMessageAsync(request, cancellationToken);
    /// <inheritdoc />
    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        => ConfirmRecoveryAsync(request, cancellationToken);
    /// <inheritdoc />
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        if (activeSql is { Restore: true, ConfirmationConsumed: true, PathConsumed: false } sql && request.Title == Resources.Open_PlaylistBackup)
        { sql.PathConsumed = true; return Task.FromResult(new UiFilePickerResult(UiDialogStatus.Accepted, [sql.Path])); }
        return inner.PickFileAsync(request, cancellationToken);
    }
    /// <inheritdoc />
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => inner.PickFolderAsync(request, cancellationToken);
    /// <inheritdoc />
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
    {
        if (activeSql is { Restore: false, PathConsumed: false } sql && request.Title == Resources.Save_PlaylistBackup)
        { sql.PathConsumed = true; return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.Accepted, sql.Path)); }
        return inner.PickSaveFileAsync(request, cancellationToken);
    }
    /// <inheritdoc />
    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        => inner.RunWithProgressAsync(request, operation, cancellationToken);
}
