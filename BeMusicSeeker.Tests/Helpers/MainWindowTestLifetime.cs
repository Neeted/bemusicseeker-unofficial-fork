using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>実MainWindowの終端、設定購読と共有Resourcesの復元を同じ所有単位へ集めます。</summary>
internal sealed class MainWindowTestLifetime : IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly Task shutdownRequested;
    private readonly bool hadPreviousResource;
    private readonly object? previousResource;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MainWindow? window;
    private bool disposed;

    /// <summary>画面構築前に共有Resourceの所有を開始します。機能側が入力と終端Taskを渡します。</summary>
    internal MainWindowTestLifetime(MainWindowViewModel viewModel, Task shutdownRequested)
    {
        this.viewModel = viewModel;
        this.shutdownRequested = shutdownRequested;
        hadPreviousResource = Application.Current.Resources.Contains("vm");
        previousResource = hadPreviousResource ? Application.Current.Resources["vm"] : null;
        Application.Current.Resources["vm"] = viewModel;
    }

    /// <summary>同じ寿命で生成した実WindowのClosedを、表示・本体実行前に購読します。</summary>
    internal MainWindow CreateWindow(Func<MainWindow> factory)
    {
        window = factory();
        window.Closed += OnClosed;
        return window;
    }

    private void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();

    /// <summary>本体の失敗を優先し、後片付け単独の失敗も表面化します。</summary>
    internal void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        try { body(); }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        DisposeAfterBodyFailure(failure?.SourceException);
        failure?.Throw();
    }

    /// <summary>本体で既に失敗した場合はその例外を保持し、回収単独の失敗は伝播します。</summary>
    internal void DisposeAfterBodyFailure(Exception? bodyFailure)
    {
        try { Dispose(); }
        catch (Exception cleanupFailure) when (bodyFailure != null)
        {
            bodyFailure.Data["MainWindowTestCleanupFailure"] = cleanupFailure.ToString();
        }
    }

    /// <summary>実shutdownとClosedを回収し、設定購読解除と共有Resource復元を必ず試みます。</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        try
        {
            if (window == null)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync(), "main-window.initialization-cleanup");
            }
            else
            {
                ExceptionDispatchInfo? terminalFailure = null;
                try
                {
                    if (!closed.Task.IsCompleted) { window.Close(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        TestUiDispatcherHost.AwaitNotificationAsync(shutdownRequested, window.CloseCompletion, "main-window.terminal-shutdown"),
                        "main-window.terminal-shutdown");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "main-window.close-completion");
                }
                catch (Exception exception) { terminalFailure = ExceptionDispatchInfo.Capture(exception); }
                try
                {
                    if (!closed.Task.IsCompleted) { window.Close(); }
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "main-window.closed");
                }
                catch (Exception cleanupFailure) when (terminalFailure != null)
                {
                    terminalFailure.SourceException.Data["MainWindowClosedCleanupFailure"] = cleanupFailure.ToString();
                }
                terminalFailure?.Throw();
            }
        }
        finally
        {
            if (window != null) { window.Closed -= OnClosed; }
            try { viewModel.SettingDialog.Dispose(); }
            finally
            {
                if (hadPreviousResource) { Application.Current.Resources["vm"] = previousResource; }
                else { Application.Current.Resources.Remove("vm"); }
            }
        }
    }
}
