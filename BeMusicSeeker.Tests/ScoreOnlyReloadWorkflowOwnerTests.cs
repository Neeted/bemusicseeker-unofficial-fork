using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ScoreOnlyReloadWorkflowOwnerTests
{
    [TestMethod]
    public async Task ReloadAsync_SuccessInvokesScoreReloadExactlyOnce()
    {
        int reloadCount = 0;
        var owner = new ScoreOnlyReloadWorkflowOwner(() =>
        {
            reloadCount++;
            return Task.CompletedTask;
        });

        await owner.ReloadAsync();

        Assert.AreEqual(1, reloadCount);
    }

    [TestMethod]
    public async Task ReloadAsync_FailurePreservesOriginalException()
    {
        var failure = new IOException("score reload failed");
        var owner = new ScoreOnlyReloadWorkflowOwner(
            () => Task.FromException(failure));

        IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => owner.ReloadAsync());

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task ReloadAsync_CancellationPreservesOriginalException()
    {
        var cancellation = new OperationCanceledException("score reload cancelled");
        var owner = new ScoreOnlyReloadWorkflowOwner(async () =>
        {
            await Task.Yield();
            throw cancellation;
        });

        OperationCanceledException thrown =
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => owner.ReloadAsync());

        Assert.AreSame(cancellation, thrown);
    }

    [TestMethod]
    public async Task MainWindowConsumer_SuccessUsesSummaryPresentationWithoutPlaylistReloadRoutes()
    {
        int scoreReloadCount = 0;
        var owner = new ScoreOnlyReloadWorkflowOwner(() =>
        {
            scoreReloadCount++;
            return Task.CompletedTask;
        });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);
        var presentationRequests = new List<PlaylistPresentationRefreshRequestedEventArgs>();
        int referenceApplyCount = 0;
        int externalSyncCount = 0;
        viewModel.PlaylistWorkspace.IsPlaylistSummaryMode = true;
        viewModel.PlaylistWorkspace.PlaylistPresentationRefreshRequested +=
            (_, request) => presentationRequests.Add(request);
        viewModel.PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queued +=
            (_, _) => referenceApplyCount++;
        viewModel.PlaylistWorkspace.PlaylistExternalSyncQueued +=
            (_, _) => externalSyncCount++;

        try
        {
            await viewModel.ReloadScoresOnlyAsync();

            Assert.AreEqual(1, scoreReloadCount);
            Assert.AreEqual(1, presentationRequests.Count);
            Assert.AreEqual(
                PlaylistPresentationRefreshKind.SummaryData,
                presentationRequests[0].Kind);
            Assert.AreEqual("score_only_reload", presentationRequests[0].Reason);
            Assert.AreEqual(0, referenceApplyCount);
            Assert.AreEqual(0, externalSyncCount);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsOperationActive);
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task MainWindowConsumer_SharedGateSerializesCallsAndPublishesDistinctTokens()
    {
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<long>();
        int reloadCount = 0;
        MainWindowViewModel? viewModel = null;
        var owner = new ScoreOnlyReloadWorkflowOwner(() =>
        {
            reloadCount++;
            tokens.Add(viewModel!.ProgressHub.StartupProgress.GetActiveStartupProgressOperationToken());
            if (reloadCount == 1)
            {
                firstEntered.SetResult();
                return releaseFirst.Task;
            }

            secondEntered.SetResult();
            return Task.CompletedTask;
        });
        viewModel = CreateMainWindowViewModel(owner);

        try
        {
            Task first = viewModel.ReloadScoresOnlyAsync();
            await firstEntered.Task;
            Task second = viewModel.ReloadScoresOnlyAsync();

            Assert.AreEqual(1, reloadCount);
            Assert.IsFalse(second.IsCompleted);

            releaseFirst.SetResult();
            await first;
            await secondEntered.Task;
            await second;

            Assert.AreEqual(2, reloadCount);
            Assert.AreEqual(2, tokens.Count);
            Assert.IsTrue(tokens[0] > 0L);
            Assert.IsTrue(tokens[1] > tokens[0]);
        }
        finally
        {
            releaseFirst.TrySetResult();
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public async Task MainWindowConsumer_FailureIsRetryableAndGateAllowsImmediateNextCall()
    {
        var failure = new IOException("score reload failed");
        int reloadCount = 0;
        var owner = new ScoreOnlyReloadWorkflowOwner(() =>
        {
            reloadCount++;
            return reloadCount == 1
                ? Task.FromException(failure)
                : Task.CompletedTask;
        });
        MainWindowViewModel viewModel = CreateMainWindowViewModel(owner);

        try
        {
            IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(
                () => viewModel.ReloadScoresOnlyAsync());

            Assert.AreSame(failure, thrown);
            Assert.AreEqual(1, reloadCount);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsFailed);
            Assert.IsTrue(viewModel.ProgressHub.StartupProgress.IsRetryableFailure);
            Assert.IsFalse(viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsFalse(viewModel.IsLibraryOperationInProgress);

            Task next = viewModel.ReloadScoresOnlyAsync();
            Assert.AreEqual(2, reloadCount);
            await next;
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }

    [TestMethod]
    public void Constructor_RejectsMissingScoreReloadOperation()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => new ScoreOnlyReloadWorkflowOwner(null));
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        ScoreOnlyReloadWorkflowOwner owner)
    {
        var composition = new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(new Settings()),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        return new MainWindowViewModel(
            composition,
            composition,
            initializationStatePort: new InitializedStatePort(),
            scoreOnlyReloadWorkflow: owner);
    }

    private sealed class InitializedStatePort : IMainWindowInitializationStatePort
    {
        public bool IsInitializationCompleted => true;
    }
}
