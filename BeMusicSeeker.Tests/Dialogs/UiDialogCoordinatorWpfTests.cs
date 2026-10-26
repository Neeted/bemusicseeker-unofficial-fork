using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parago.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UiDialogCoordinatorWpfTests
{
    /// <summary>実modalへ解決ownerを渡し、肯定結果・戻り値と表示終了を保持して所有者の取り違えを防ぎます。</summary>
    [TestMethod]
    public void ShowWindowAsyncAssignsOwnerAndMapsDialogResult()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            ProbeWindow? created = null;

            UiWindowDialogResult<string> result = CreateTestCoordinator(windowTest).ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () =>
                    {
                        created = new ProbeWindow();
                        created.Loaded += (_, _) => created.DialogResult = true;
                        return created;
                    },
                    window => window.Owner == owner ? "accepted-by-owner" : "wrong-owner",
                    owner)).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.Accepted, result.Status);
            Assert.IsTrue(result.IsAccepted);
            Assert.IsTrue(result.DialogResult.GetValueOrDefault());
            Assert.AreEqual("accepted-by-owner", result.Value);
            Assert.IsNotNull(created);
            Assert.AreSame(owner, created!.Owner);
            Assert.IsFalse(created!.IsVisible);
        });
    }

    /// <summary>factory未生成・別owner指定・owner不在を区別して失敗し、owner不在ではfactoryも開始しません。</summary>
    [TestMethod]
    public void ShowWindowAsyncPreservesFactoryAndOwnerFailures()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            UiDialogCoordinator coordinator = CreateTestCoordinator(windowTest);

            UiWindowDialogResult<string> nullFactory = coordinator.ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () => null!,
                    _ => "unused",
                    owner)).GetAwaiter().GetResult();
            Assert.AreEqual(UiDialogStatus.Failed, nullFactory.Status);
            StringAssert.Contains(nullFactory.Error?.Message, "factory returned null");

            Window differentOwner = CreateTestWindow("different-owner");
            windowTest.ShowAndWaitForContentRendered(differentOwner);
            UiWindowDialogResult<string> differentOwnerResult = coordinator.ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () => new ProbeWindow { Owner = differentOwner },
                    _ => "unused",
                    owner)).GetAwaiter().GetResult();
            Assert.AreEqual(UiDialogStatus.Failed, differentOwnerResult.Status);
            StringAssert.Contains(differentOwnerResult.Error?.Message, "assigned a different owner");

            var unavailableCoordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(() => null));
            bool factoryCalled = false;
            UiWindowDialogResult<string> ownerUnavailable = unavailableCoordinator.ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () =>
                    {
                        factoryCalled = true;
                        return new ProbeWindow();
                    },
                    _ => "unused")).GetAwaiter().GetResult();
            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, ownerUnavailable.Status);
            Assert.IsFalse(factoryCalled);
        });
    }

    /// <summary>実modal表示中はそのwindowを親として優先し、閉鎖後は元ownerへ戻って閉じた親を残しません。</summary>
    [TestMethod]
    public void ActiveModalOwnerTakesPrecedenceAndIsReleasedAfterScope()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window requestedOwner = CreateTestWindow("requested");
            windowTest.ShowAndWaitForContentRendered(requestedOwner);
            var resolver = new UiDialogOwnerResolver(() => Application.Current);
            ProbeWindow? created = null;
            Window? observedActiveOwner = null;

            UiWindowDialogResult<string> result = CreateTestCoordinator(windowTest, resolver).ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () =>
                    {
                        created = new ProbeWindow();
                        created.Loaded += (_, _) =>
                        {
                            observedActiveOwner = resolver.ResolveOwner(requestedOwner);
                            created.DialogResult = true;
                        };
                        return created;
                    },
                    _ => "active-modal-probe",
                    requestedOwner)).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.Accepted, result.Status);
            Assert.IsNotNull(created);
            Assert.AreSame(created, observedActiveOwner);
            Assert.AreSame(requestedOwner, resolver.ResolveOwner(requestedOwner));
        });
    }

    /// <summary>実進捗dialogから本体へ明示contextを渡し、報告を通した成功完了をAcceptedとして返します。</summary>
    [TestMethod]
    public void RunWithProgressAsyncPassesExplicitContextAndCompletesAccepted()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            var reports = new List<string>();
            var settings = new ProgressDialogSettings(
                showSubLabel: true,
                showCancelButton: false,
                showProgressBarIndeterminate: false);

            UiProgressResult result = CreateTestCoordinator(windowTest).RunWithProgressAsync(
                new UiProgressRequest("Progress", "Working", settings, owner),
                context =>
                {
                    context.ReportWithCancellationCheck(50, "half");
                    reports.Add("context-present");
                    return System.Threading.Tasks.Task.CompletedTask;
                }).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.Accepted, result.Status);
            CollectionAssert.AreEqual(new[] { "context-present" }, reports);
        });
    }

    /// <summary>owner不在の通知・確認・進捗要求をOwnerUnavailableで終え、利用者による取消や正常完了へ置換しません。</summary>
    [TestMethod]
    public void MessageAndProgressRoutesPreserveOwnerUnavailableStatus()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var coordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(() => null));
            UiDialogResult message = coordinator.ShowMessageAsync(
                new UiMessageRequest("message", "caption", UiDialogButton.OK, UiDialogIcon.Information)).GetAwaiter().GetResult();
            UiDialogResult confirmation = coordinator.ConfirmAsync(
                new UiConfirmationRequest("confirm", "caption", UiDialogButton.YesNo, UiDialogIcon.Question)).GetAwaiter().GetResult();
            UiProgressResult progress = coordinator.RunWithProgressAsync(
                new UiProgressRequest("progress", "label"),
                _ => System.Threading.Tasks.Task.CompletedTask).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, message.Status);
            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, confirmation.Status);
            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, progress.Status);
        });
    }

    /// <summary>実ownerを解決してUI所属presenterへ正確な要求を渡し、肯定・否定・選択なし閉鎖の結果を区別します。</summary>
    [TestMethod]
    public void MessagePresenterReceivesResolvedOwnerAndExactRequestsAndMapsOutcomes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);

            var messageRequest = new UiMessageRequest(
                "message",
                "caption",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                MessageBoxResult.OK,
                owner: owner);
            var confirmationRequest = new UiConfirmationRequest(
                "confirm",
                "caption",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No,
                owner: owner);
            var closedRequest = new UiMessageRequest(
                "closed",
                "caption",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                MessageBoxResult.OK,
                owner: owner);
            var requests = new List<(Window Owner, UiMessageRequest Request, int ManagedThreadId)>();
            var responses = new Queue<ThemedMessageBoxResponse>(
            [
                new(MessageBoxResult.OK, closedWithoutSelection: false),
                new(MessageBoxResult.No, closedWithoutSelection: false),
                new(MessageBoxResult.None, closedWithoutSelection: true),
            ]);
            UiDialogCoordinator coordinator = CreateTestCoordinator(
                windowTest,
                messagePresenter: (resolvedOwner, request) =>
                {
                    requests.Add((resolvedOwner, request, Thread.CurrentThread.ManagedThreadId));
                    return responses.Dequeue();
                });

            UiDialogResult accepted = coordinator.ShowMessageAsync(messageRequest).GetAwaiter().GetResult();
            UiDialogResult rejected = coordinator.ConfirmAsync(confirmationRequest).GetAwaiter().GetResult();
            UiDialogResult closed = coordinator.ShowMessageAsync(closedRequest).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.Accepted, accepted.Status);
            Assert.AreEqual(UiDialogStatus.Rejected, rejected.Status);
            Assert.AreEqual(UiDialogStatus.ClosedByUser, closed.Status);
            Assert.AreSame(owner, requests[0].Owner);
            Assert.AreSame(messageRequest, requests[0].Request);
            Assert.AreSame(owner, requests[1].Owner);
            Assert.AreSame(confirmationRequest, requests[1].Request);
            Assert.IsInstanceOfType(requests[1].Request, typeof(UiConfirmationRequest));
            Assert.AreSame(owner, requests[2].Owner);
            Assert.AreSame(closedRequest, requests[2].Request);
            Assert.IsTrue(requests.All(call => call.ManagedThreadId == TestUiDispatcherHost.Dispatcher.Thread.ManagedThreadId));
        });
    }

    /// <summary>実ownerを解決したpresenterの元例外をFailedへ渡し、別表示や正常応答で失敗を隠しません。</summary>
    [TestMethod]
    public void MessagePresenterExceptionBecomesFailedWithoutFallback()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            var expected = new InvalidOperationException("message presenter failed");
            bool presenterCalled = false;
            UiDialogCoordinator coordinator = CreateTestCoordinator(
                windowTest,
                messagePresenter: (resolvedOwner, request) =>
                {
                    presenterCalled = true;
                    Assert.AreSame(owner, resolvedOwner);
                    throw expected;
                });

            UiDialogResult result = coordinator.ShowMessageAsync(
                new UiMessageRequest(
                    "message",
                    "caption",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    MessageBoxResult.OK,
                    owner: owner)).GetAwaiter().GetResult();

            Assert.IsTrue(presenterCalled);
            Assert.AreEqual(UiDialogStatus.Failed, result.Status);
            Assert.AreSame(expected, result.Exception);
            Assert.AreEqual(MessageBoxResult.None, result.MessageBoxResult);
        });
    }

    /// <summary>3択の実modalからNo/Cancel/Close/Escを生産し、既定Yes付きCloseと明示Yesの区別を受信ownerへ渡します。</summary>
    [DataTestMethod]
    [DataRow("no")]
    [DataRow("cancel")]
    [DataRow("close")]
    [DataRow("escape")]
    public void WholeFolderConfirmation_RealMessageProducesDistinctNoCancelAndClose(string operationKind)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window owner = CreateTestWindow("whole-folder-owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Window? dialog = null;
            bool scheduled = false;
            int cancelClicks = 0;
            var coordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(), window =>
            {
                dialog = window;
                windowTest.PrepareForOwnedPresentation(window);
                window.Closed += (_, _) => closed.TrySetResult(true);
                window.ContentRendered += (_, _) =>
                {
                    rendered.TrySetResult(true);
                    if (scheduled) { return; }
                    scheduled = true;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Exception? operationFailure = null;
                        try
                        {
                            Assert.AreSame(owner, window.Owner);
                            Button[] buttons = EnumerateButtons(window).ToArray();
                            Button yes = buttons.Single(button => AutomationProperties.GetAutomationId(button) == "ThemedMessageBoxYes");
                            Button no = buttons.Single(button => AutomationProperties.GetAutomationId(button) == "ThemedMessageBoxNo");
                            Button cancel = buttons.Single(button => AutomationProperties.GetAutomationId(button) == "ThemedMessageBoxCancel");
                            Assert.IsTrue(yes.IsDefault);
                            Assert.IsFalse(no.IsCancel);
                            Assert.IsTrue(cancel.IsCancel);
                            cancel.Click += (_, _) => cancelClicks++;
                            switch (operationKind)
                            {
                                case "no": no.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                                case "cancel": cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
                                case "close": window.Close(); break;
                                case "escape":
                                    PresentationSource source = PresentationSource.FromVisual(window)
                                        ?? throw new InvalidOperationException("実modalのPresentationSourceがありません。");
                                    AccessKeyManager.ProcessKey(source, "\u001B", false);
                                    Assert.AreEqual(1, cancelClicks, "Escは実CancelボタンのClickへ配送される必要があります。");
                                    break;
                                default: throw new ArgumentOutOfRangeException(nameof(operationKind));
                            }
                        }
                        catch (Exception failure) { operationFailure = failure; }
                        finally
                        {
                            // 操作が失敗しても所有modalを回収し、元の失敗を閉鎖失敗で置き換えません。
                            if (window.IsVisible)
                            {
                                operationFailure ??= new InvalidOperationException("UI操作後も実modalが閉鎖されませんでした。");
                                try { window.Close(); }
                                catch (Exception) { }
                            }
                        }
                        if (operationFailure != null) { operated.TrySetException(operationFailure); }
                        else { operated.TrySetResult(true); }
                    }));
                };
                return UiDialogOwnerResolver.PushActiveModal(window);
            });
            Task<UiDialogResult> operation = Task.Run(() => coordinator.ConfirmAsync(new UiConfirmationRequest(
                "Delete this whole folder?", "Confirm", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                MessageBoxResult.Yes, owner: owner)));
            Exception? primaryFailure = null;
            try
            {
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(Task.WhenAny(rendered.Task, operation), "whole-folder render or failure");
                Assert.IsTrue(rendered.Task.IsCompletedSuccessfully, "実modalの描画前に確認Taskが終了しました。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operated.Task, "whole-folder UI operation");
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "whole-folder Closed");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "whole-folder coordinator outcome");
                UiDialogResult result = operation.GetAwaiter().GetResult();
                Assert.AreEqual(operationKind == "no" ? UiDialogStatus.Rejected : operationKind == "close"
                    ? UiDialogStatus.ClosedByUser : UiDialogStatus.CancelledByUser, result.Status);
                Assert.AreEqual(operationKind == "no" ? MessageBoxResult.No : operationKind == "close"
                    ? MessageBoxResult.Yes : MessageBoxResult.Cancel, result.MessageBoxResult);
                Assert.AreEqual(operationKind is "cancel" or "escape" ? 1 : 0, cancelClicks);
                Assert.IsNull(result.Exception);
                Assert.IsNotNull(dialog);
                Assert.IsFalse(dialog.IsVisible);
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                try
                {
                    if (dialog?.IsVisible == true) { dialog.Close(); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "whole-folder coordinator cleanup");
                }
                catch (Exception) when (primaryFailure != null) { }
            }
        });

        static IEnumerable<Button> EnumerateButtons(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is Button button) { yield return button; }
                foreach (Button descendant in EnumerateButtons(child)) { yield return descendant; }
            }
        }
    }

    private static UiDialogCoordinator CreateTestCoordinator(
        TestWindowPresentationScope windowTest,
        UiDialogOwnerResolver? ownerResolver = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse>? messagePresenter = null)
    {
        return new UiDialogCoordinator(
            ownerResolver ?? new UiDialogOwnerResolver(),
            window =>
            {
                windowTest.PrepareForOwnedPresentation(window);
                return UiDialogOwnerResolver.PushActiveModal(window);
            },
            messagePresenter ?? ((owner, request) =>
                ThemedMessageBox.ShowWithStatus(
                    owner,
                    request.MessageBoxText,
                    request.Caption,
                    request.Button,
                    request.Icon,
                    request.DefaultResult,
                    request.Options,
                    request.WarningMessageBoxText)));
    }

    private static Window CreateTestWindow(string title)
    {
        return new Window
        {
            Title = title,
            Width = 320,
            Height = 180,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new System.Windows.Controls.Border()
        };
    }

    private sealed class ProbeWindow : Window
    {
        internal ProbeWindow()
        {
            Width = 280;
            Height = 160;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Content = new System.Windows.Controls.Border();
        }
    }
}
