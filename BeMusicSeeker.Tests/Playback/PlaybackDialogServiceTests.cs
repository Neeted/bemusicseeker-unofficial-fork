using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BmsAudioFrameEvent = Ribbit.BMS.BmsAudioFrameEvent;
using BmsScheduledAudioException = Ribbit.BMS.BmsScheduledAudioException;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackDialogServiceTests
{
    /// <summary>
    /// 一時導入の再生確認は警告リソースと要求形式を維持し、Yesだけを受理して拒否・取消・閉鎖・Dispatcher不在を区別する。
    /// </summary>
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

    /// <summary>
    /// 再生確認の表示に失敗したとき、元例外を返し利用者の拒否へ置き換えて隠さない。
    /// </summary>
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

    /// <summary>
    /// 再生失敗は内部schedule診断を表示へ漏らさずリソースで通知し、閉鎖・不在・表示失敗・不正な応答を通知契約に従って扱う。
    /// </summary>
    [TestMethod]
    public void NotifyPlaybackFailure_PreservesRequestAndResultContract()
    {
        var playbackFailure = new BmsScheduledAudioException(
            "schedule",
            new BmsAudioFrameEvent(3, TimeSpan.FromSeconds(1), 48000, null, 0),
            segmentStartSongFrame: 0,
            segmentStartMixerFrame: 128,
            originMixerFrame: 128,
            expectedMixerFrame: 48128,
            currentMixerFrame: 48128,
            sampleRate: 48000,
            channelCount: 2,
            generation: 1);
        StringAssert.Contains(playbackFailure.Message, "stage=schedule");
        var dialogs = new FakeUiDialogService
        {
            MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var service = new WpfPlaybackDialogService(dialogs);

        service.NotifyPlaybackFailure(playbackFailure);

        UiMessageRequest request = dialogs.MessageRequest ?? throw new AssertFailedException("Message request was not captured.");
        Assert.AreEqual(Resources.Msg_failed_play, request.MessageBoxText);
        Assert.IsFalse(request.MessageBoxText.Contains("stage=schedule", StringComparison.Ordinal));
        StringAssert.Contains(playbackFailure.Message, "stage=schedule");
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
