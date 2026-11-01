using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>複数の設定テストで同じ確認・選択要求を観測します。</summary>
internal sealed class RecordingRootDialogService : IUiDialogService
{
    internal Queue<UiFilePickerResult> FileResults { get; } = [];
    internal Queue<UiFolderPickerResult> FolderResults { get; } = [];
    internal List<UiFolderPickerRequest> FolderRequests { get; } = [];

    internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

    internal UiDialogResult? MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

    internal Action? MessageObserved { get; set; }

    internal Action<UiConfirmationRequest>? ConfirmationObserved { get; set; }

    internal Func<Task>? ConfirmationObservedAsync { get; set; }

    internal Func<Task>? MessageObservedAsync { get; set; }

    internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

    internal int ConfirmationCount { get; private set; }

    internal int MessageCount { get; private set; }

    internal string LastMessageText { get; private set; } = string.Empty;

    public Task<UiDialogResult?> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        MessageCount++;
        LastMessageText = request.MessageBoxText;
        MessageObserved?.Invoke();
        return CompleteMessageAsync();
    }

    private async Task<UiDialogResult?> CompleteMessageAsync()
    {
        if (MessageObservedAsync != null)
        {
            await MessageObservedAsync();
        }
        return MessageResult;
    }

    public async Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ConfirmationCount++;
        ConfirmationRequests.Add(request);
        ConfirmationObserved?.Invoke(request);
        if (ConfirmationObservedAsync != null) { await ConfirmationObservedAsync(); }
        return ConfirmationResult;
    }

    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
        UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default)
        where TWindow : Window => throw new NotSupportedException();

    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
        FileResults.Count > 0 ? Task.FromResult(FileResults.Dequeue()) : throw new NotSupportedException();

    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
    {
        FolderRequests.Add(request);
        return FolderResults.Count > 0 ? Task.FromResult(FolderResults.Dequeue()) : throw new NotSupportedException();
    }

    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<UiProgressResult> RunWithProgressAsync(
        UiProgressRequest request,
        Func<UiProgressContext, Task> operation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>複数の設定テストで同じ保存回数と保存終端を観測します。</summary>
internal sealed class CountingSettingsEditSession : ISettingsEditSession
{
    public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
    {
        environment.Commit(Values);
        Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
        Save();
        Reload();
    }

    internal CountingSettingsEditSession(Settings values)
    {
        Values = values;
    }

    internal Action? SaveObserved { get; set; }

    internal Exception? SaveFailure { get; set; }

    internal bool BlockSave { get; set; }

    internal bool PersistOwnedValues { get; init; }

    internal ManualResetEventSlim SaveEntered { get; } = new(false);

    internal ManualResetEventSlim ReleaseSave { get; } = new(false);

    internal int SaveCount { get; private set; }

    public Settings Values { get; }

    public void Reload()
    {
        if (PersistOwnedValues) { Values.Reload(); }
    }

    public void Save()
    {
        SaveCount++;
        SaveObserved?.Invoke();
        if (SaveFailure != null)
        {
            throw SaveFailure;
        }
        if (BlockSave)
        {
            SaveEntered.Set();
            ReleaseSave.Wait();
        }
        if (PersistOwnedValues) { Values.Save(); }
    }
}

/// <summary>保存要求を実providerへ一回委譲し、元例外と保存結果をそのまま返す局所観測です。</summary>
internal sealed class InstallAdmissionObservingSettingsProvider : System.Configuration.SettingsProvider
{
    private readonly System.Configuration.SettingsProvider inner;
    private readonly Action observe;
    internal InstallAdmissionObservingSettingsProvider(System.Configuration.SettingsProvider inner, Action observe)
    {
        this.inner = inner; this.observe = observe;
        Initialize(nameof(InstallAdmissionObservingSettingsProvider), new System.Collections.Specialized.NameValueCollection());
    }
    public override string ApplicationName { get => inner.ApplicationName; set => inner.ApplicationName = value; }
    /// <summary>同期保存の要求回数です。初期表示や再表示からの追加保存を検出します。</summary>
    internal int SaveCount { get; private set; }
    public override System.Configuration.SettingsPropertyValueCollection GetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyCollection properties)
        => inner.GetPropertyValues(context, properties);
    public override void SetPropertyValues(System.Configuration.SettingsContext context, System.Configuration.SettingsPropertyValueCollection values)
    {
        SaveCount++;
        observe();
        try { inner.SetPropertyValues(context, values); }
        finally { observe(); }
    }
}

/// <summary>各操作の前に捕捉した実走査入力へ切り替える、設定・モーダル代表の限定fixtureです。</summary>
internal sealed class SettingsChartFileScanner(IChartFileScanner initial) : IChartFileScanner
{
    /// <summary>次の実走査へ渡す、fixtureで捕捉した現在のファイル入力です。</summary>
    internal IChartFileScanner Current { get; set; } = initial;
    /// <summary>実走査へ渡したrootを、固定走査結果と独立に観測します。</summary>
    internal Action<IReadOnlyList<string>>? RootsObserved { get; set; }
    public ChartScanExecutionResult Scan(IEnumerable<string> roots, IEnumerable<string> extensions, bool verboseLog = false,
        bool includeTextSurface = true, bool includeDirectorySurface = false)
    {
        string[] capturedRoots = roots.ToArray();
        RootsObserved?.Invoke(capturedRoots);
        return Current.Scan(capturedRoots, extensions, verboseLog, includeTextSurface, includeDirectorySurface);
    }
}
