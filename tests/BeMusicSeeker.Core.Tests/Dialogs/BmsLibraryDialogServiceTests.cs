using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>
/// モデルが持つ窓口の型と、構成が注入する View 側の実装の境界を検証します。
/// 結果の変換は注入した記録用の <see cref="IUiDialogService"/> で確かめ、実ウィンドウを使いません。
/// </summary>
[TestClass]
public sealed class BmsLibraryDialogServiceTests
{
    /// <summary>OKだけの通知は通知経路へ、それ以外のボタンは確認経路へ渡し、受理・拒否・取消・閉鎖を元の既定結果のまま返します。</summary>
    [TestMethod]
    [DataRow((int)UiDialogButton.OK, MessageBoxResult.OK, (int)UiDialogDefaultResult.OK, true)]
    [DataRow((int)UiDialogButton.YesNo, MessageBoxResult.Yes, (int)UiDialogDefaultResult.Yes, false)]
    [DataRow((int)UiDialogButton.YesNo, MessageBoxResult.No, (int)UiDialogDefaultResult.No, false)]
    [DataRow((int)UiDialogButton.OKCancel, MessageBoxResult.Cancel, (int)UiDialogDefaultResult.Cancel, false)]
    [DataRow((int)UiDialogButton.YesNoCancel, MessageBoxResult.None, (int)UiDialogDefaultResult.None, false)]
    public void Show_RoutesByButtonAndReturnsUserChoice(int buttonValue, MessageBoxResult shown, int expectedValue, bool message)
    {
        // 列挙型は内部型のため、テストの引数は数値で受けます。
        var button = (UiDialogButton)buttonValue;
        var expected = (UiDialogDefaultResult)expectedValue;
        var inner = new RecordingUiDialogs(UiDialogResult.FromMessageBoxResult(shown));
        var service = new BmsLibraryDialogService(inner);

        UiDialogDefaultResult actual = service.Show("本文", "題名", button, UiDialogIcon.Question, UiDialogDefaultResult.No);

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(message ? 1 : 0, inner.MessageRequests.Count);
        Assert.AreEqual(message ? 0 : 1, inner.ConfirmRequests.Count);
        UiMessageRequest request = message ? inner.MessageRequests.Single() : inner.ConfirmRequests.Single();
        Assert.AreEqual("本文", request.MessageBoxText);
        Assert.AreEqual("題名", request.Caption);
        Assert.AreEqual(Enum.Parse<MessageBoxButton>(button.ToString()), request.Button);
        Assert.AreEqual(MessageBoxImage.Question, request.Icon);
        Assert.AreEqual(MessageBoxResult.No, request.DefaultResult);
    }

    /// <summary>表示中の失敗は元例外を保持した失敗にし、表示されなかった結果は拒否や取消へ丸めず状態名つきの失敗にします。</summary>
    [TestMethod]
    public void Show_ConvertsFailureAndNotShownToExceptions()
    {
        var cause = new InvalidOperationException("表示失敗");
        var failed = new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.Failed(cause)));
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => failed.Show("本文", "題名", UiDialogButton.OK, UiDialogIcon.None));
        Assert.AreSame(cause, failure.InnerException);

        var notShown = new BmsLibraryDialogService(new RecordingUiDialogs(UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)));
        InvalidOperationException hidden = Assert.ThrowsExactly<InvalidOperationException>(
            () => notShown.Show("本文", "題名", UiDialogButton.YesNo, UiDialogIcon.None));
        StringAssert.Contains(hidden.Message, nameof(UiDialogStatus.OwnerUnavailable));
        Assert.IsNull(hidden.InnerException);
    }

    /// <summary>実装は注入された表示窓口を必須にし、自分で実物を作りません。</summary>
    [TestMethod]
    public void Constructor_RequiresInjectedUiDialogService()
    {
        IUiDialogService? missing = null;
        Assert.ThrowsExactly<ArgumentNullException>(() => new BmsLibraryDialogService(missing));
    }

    /// <summary>ライブラリは窓口を必須で受け取り、モデルが既定の実装を作って未注入を隠しません。</summary>
    [TestMethod]
    public void BMSLibrary_RejectsMissingDialogService()
    {
        // 窓口以外は有効な引数にし、引数検査の順序ではなく窓口の欠落そのものが拒否の原因であることを確かめます。
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string songDbPath = Path.Combine(workspace.RootDirectory, "song.db");
        File.WriteAllBytes(songDbPath, []);
        BeMusicSeeker.Properties.Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = false);
        IBmsLibraryDialogService? missing = null;

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(
            () => new BMSLibrary(
                new ApplicationDatabaseScope(),
                new BmsDatabaseLoadOptions(true, true, true),
                songDbPath,
                null,
                null,
                null,
                missing,
                null,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
                TestUiScheduler.CreateDefault(),
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(workspace.RootDirectory, "BeMusicSeeker.exe")),
                new BmsRootRegistrationStore(settings, () => null)));
        Assert.AreEqual("dialogService", exception.ParamName);
    }

    /// <summary>通知と確認の要求だけを記録して固定の結果を返す、表示窓口の記録用実装です。</summary>
    private sealed class RecordingUiDialogs(UiDialogResult result) : IUiDialogService
    {
        internal List<UiMessageRequest> MessageRequests { get; } = [];
        internal List<UiConfirmationRequest> ConfirmRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            return Task.FromResult(result);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmRequests.Add(request);
            return Task.FromResult(result);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
