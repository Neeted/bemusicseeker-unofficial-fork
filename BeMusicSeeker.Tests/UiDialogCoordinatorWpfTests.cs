using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
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
    [TestMethod]
    public void ShowWindowAsyncAssignsOwnerAndMapsDialogResult()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            ProbeWindow created = null;

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
            Assert.AreSame(owner, created.Owner);
            Assert.IsFalse(created.IsVisible);
        });
    }

    [TestMethod]
    public void ShowWindowAsyncPreservesFactoryAndOwnerFailures()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var owner = CreateTestWindow("owner");
            windowTest.ShowAndWaitForContentRendered(owner);
            var coordinator = CreateTestCoordinator(windowTest);

            UiWindowDialogResult<string> nullFactory = coordinator.ShowWindowAsync(
                new UiWindowDialogRequest<ProbeWindow, string>(
                    () => null,
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

    [TestMethod]
    public void ActiveModalOwnerTakesPrecedenceAndIsReleasedAfterScope()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var requestedOwner = CreateTestWindow("requested");
            windowTest.ShowAndWaitForContentRendered(requestedOwner);
            var resolver = new UiDialogOwnerResolver(() => Application.Current);
            ProbeWindow created = null;
            Window observedActiveOwner = null;

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

    [TestMethod]
    public void RunWithProgressAsyncPassesExplicitContextAndCompletesAccepted()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var owner = CreateTestWindow("owner");
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

    [TestMethod]
    public void MessageAndProgressRoutesPreserveOwnerUnavailableStatus()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
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

    private static UiDialogCoordinator CreateTestCoordinator(
        TestWindowPresentationScope windowTest,
        UiDialogOwnerResolver ownerResolver = null,
        Func<Window, UiMessageRequest, ThemedMessageBoxResponse> messagePresenter = null)
    {
        return new UiDialogCoordinator(
            ownerResolver ?? new UiDialogOwnerResolver(),
            window =>
            {
                windowTest.PrepareForOwnedPresentation(window, TestWindowActivation.NonActivating);
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
