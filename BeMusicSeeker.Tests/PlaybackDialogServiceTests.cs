using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackDialogServiceTests
{
    [TestMethod]
    public void ConfirmTemporaryInstallPlayback_MapsUserChoiceAndPreservesRequest()
    {
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        var service = new WpfPlaybackDialogService(dialogs);

        Assert.IsTrue(service.ConfirmTemporaryInstallPlayback());
        UiConfirmationRequest request = dialogs.ConfirmationRequest ?? throw new AssertFailedException("Confirmation request was not captured.");
        Assert.AreEqual(Resources.Msg_warn_play_temp_install, request.MessageBoxText);
        Assert.AreEqual(Resources.Warning, request.Caption);
        Assert.AreEqual(MessageBoxButton.YesNo, request.Button);
        Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
        Assert.AreEqual(MessageBoxResult.None, request.DefaultResult);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
        Assert.IsFalse(service.ConfirmTemporaryInstallPlayback());
        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);
        Assert.IsFalse(service.ConfirmTemporaryInstallPlayback());
        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.None);
        Assert.IsFalse(service.ConfirmTemporaryInstallPlayback());

        dialogs.ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable);
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => service.ConfirmTemporaryInstallPlayback()).Message,
            UiDialogStatus.DispatcherUnavailable.ToString());
    }

    [TestMethod]
    public void ConfirmTemporaryInstallPlayback_DoesNotHideDisplayFailure()
    {
        var failure = new InvalidOperationException("dialog unavailable");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var service = new WpfPlaybackDialogService(dialogs);

        InvalidOperationException actual = Assert.ThrowsException<InvalidOperationException>(
            () => service.ConfirmTemporaryInstallPlayback());

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    public void NotifyPlaybackFailure_PreservesRequestAndResultContract()
    {
        var playbackFailure = new IOException("bad chart");
        var dialogs = new FakeUiDialogService
        {
            MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var service = new WpfPlaybackDialogService(dialogs);

        service.NotifyPlaybackFailure(playbackFailure);

        UiMessageRequest request = dialogs.MessageRequest ?? throw new AssertFailedException("Message request was not captured.");
        Assert.AreEqual(Resources.Msg_failed_play + Environment.NewLine + playbackFailure.Message, request.MessageBoxText);
        Assert.AreEqual(Resources.Error, request.Caption);
        Assert.AreEqual(MessageBoxButton.OK, request.Button);
        Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
        Assert.AreEqual(MessageBoxResult.OK, request.DefaultResult);

        dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
        Assert.ThrowsException<InvalidOperationException>(() => service.NotifyPlaybackFailure(playbackFailure));

        dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);
        service.NotifyPlaybackFailure(playbackFailure);
        dialogs.MessageResult = UiDialogResult.ClosedByUser(MessageBoxResult.None);
        service.NotifyPlaybackFailure(playbackFailure);

        dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable);
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => service.NotifyPlaybackFailure(playbackFailure)).Message,
            UiDialogStatus.OwnerUnavailable.ToString());

        var displayFailure = new InvalidOperationException("message display failed");
        dialogs.MessageResult = UiDialogResult.Failed(displayFailure);
        Assert.AreSame(
            displayFailure,
            Assert.ThrowsException<InvalidOperationException>(() => service.NotifyPlaybackFailure(playbackFailure)));
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? MessageResult { get; set; }

        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiMessageRequest? MessageRequest { get; private set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequest = request;
            return Task.FromResult(MessageResult ?? throw new InvalidOperationException("Message result was not configured."));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            return Task.FromResult(ConfirmationResult ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
