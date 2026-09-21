using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ElevatedProcessWarningWorkflowOwnerTests
{
    [TestMethod]
    public async Task NonElevatedProcess_IsSuppressedWithoutPresentation()
    {
        int probeCount = 0;
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeCount++;
            return false;
        });
        owner.PresentationRequested += _ => presentationCount++;

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.AreEqual(1, probeCount);
        Assert.AreEqual(0, presentationCount);
    }

    [TestMethod]
    public async Task ElevatedProcess_PresentsOnceAndPublishesDetectionAndShownLogs()
    {
        var infoMessages = new List<string>();
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            infoLog: infoMessages.Add);
        owner.PresentationRequested += request =>
        {
            presentationCount++;
            request.Complete(true);
        };

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Shown, await owner.Completion);
        Assert.AreEqual(1, presentationCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "process_elevated drag_drop_limited_warning_detected=true",
                "process_elevated drag_drop_limited_warning_shown=true"
            },
            infoMessages);
    }

    [TestMethod]
    public async Task Start_IsOneShot()
    {
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);
        owner.PresentationRequested += request =>
        {
            presentationCount++;
            request.Complete(false);
        };

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.IsFalse(owner.Start(() => true));
        Assert.AreEqual(1, presentationCount);
    }

    [TestMethod]
    public async Task MissingPresentationRoute_IsReportedAsWorkflowFailure()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated_warning_presentation_unavailable" },
            warningContexts);
    }

    [TestMethod]
    public async Task NullPresentationGuard_DoesNotCorruptOwnerState()
    {
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);

        Assert.ThrowsException<ArgumentNullException>(() => owner.Start((Func<bool>)null!));
        Assert.IsTrue(owner.Start(() => false));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
    }

    [TestMethod]
    public async Task PresentationGuardFailure_SuppressesWarningWithoutProbe()
    {
        int probeCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeCount++;
            return true;
        });

        Assert.IsTrue(owner.Start(() => false));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.AreEqual(0, probeCount);
    }

    [TestMethod]
    public async Task ProbeFailure_IsReportedAndDoesNotRequestPresentation()
    {
        var warningContexts = new List<string>();
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => throw new InvalidOperationException("probe failed"),
            warningLog: (_, context) => warningContexts.Add(context));
        owner.PresentationRequested += _ => presentationCount++;

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.ProbeFailed, await owner.Completion);
        CollectionAssert.AreEqual(new[] { "process_elevation_check_failed" }, warningContexts);
        Assert.AreEqual(0, presentationCount);
    }

    [TestMethod]
    public async Task PresentationGuardFailure_IsReportedAsWorkflowFailure()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));

        Assert.IsTrue(owner.Start(() => throw new InvalidOperationException("window state failed")));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated_warning_workflow_failed" },
            warningContexts);
    }

    [TestMethod]
    public async Task PresentationFailure_IsReportedAsWarning()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));
        owner.PresentationRequested += request => request.Fail(new InvalidOperationException("dialog failed"));

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated drag_drop_limited_warning_failed" },
            warningContexts);
    }

    [TestMethod]
    public async Task ClosingWhileProbeIsPending_SuppressesPresentation()
    {
        var probeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeEntered.TrySetResult(true);
            probeRelease.Task.GetAwaiter().GetResult();
            return true;
        });
        owner.PresentationRequested += _ => presentationCount++;

        Task<bool> startTask = Task.Run(() => owner.Start(() => true));
        await probeEntered.Task;
        Assert.IsTrue(owner.NotifyClosing());
        probeRelease.SetResult(true);
        Assert.IsTrue(await startTask);

        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Closing, await owner.Completion);
        Assert.AreEqual(0, presentationCount);
    }

    [TestMethod]
    public async Task ClosingWhilePresentationIsPending_DropsShownResult()
    {
        var requestReceived = new TaskCompletionSource<ElevatedProcessWarningPresentationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);
        owner.PresentationRequested += request => requestReceived.SetResult(request);

        Assert.IsTrue(owner.Start(() => true));
        ElevatedProcessWarningPresentationRequest request = await requestReceived.Task;
        Assert.IsTrue(owner.NotifyClosing());
        request.Complete(true);

        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Closing, await owner.Completion);
    }
}
