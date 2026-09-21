using System;
using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DroppedInstallDropTerminalTests
{
    [TestMethod]
    public void Evaluate_SuccessPublishesCopyAndTreeExpansionOnlyAfterAcquisition()
    {
        string[]? acquiredPaths = null;
        var data = new TestDataObject(["external.bms"]);

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: false,
            paths =>
            {
                acquiredPaths = paths;
                return DroppedInstallIngressAcquisitionResult.Success(
                    new DroppedInstallBatchRequest(paths));
            });

        CollectionAssert.AreEqual(new[] { "external.bms" }, acquiredPaths);
        Assert.AreEqual(DragDropEffects.Copy, decision.Effects);
        Assert.IsTrue(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.None, decision.WarningKind);
        Assert.IsFalse(decision.LogUnsupportedFormats);
    }

    [TestMethod]
    public void Evaluate_UnsupportedFormatPublishesNoneAndVisibleUnsupportedWarning()
    {
        var data = new TestDataObject(null, dataPresent: false);
        bool acquisitionCalled = false;

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: false,
            _ =>
            {
                acquisitionCalled = true;
                throw new InvalidOperationException();
            });

        Assert.IsFalse(acquisitionCalled);
        Assert.AreEqual(DragDropEffects.None, decision.Effects);
        Assert.IsFalse(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.UnsupportedFormat, decision.WarningKind);
        Assert.IsTrue(decision.LogUnsupportedFormats);
    }

    [TestMethod]
    public void Evaluate_PresenceProbeFailurePublishesNoneAndVisibleIngressWarning()
    {
        var expected = new InvalidOperationException("OLE provider failed");
        var data = new TestDataObject(null, presenceFailure: expected);

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: false,
            _ => throw new AssertFailedException("Acquisition must not run."));

        Assert.AreEqual(DragDropEffects.None, decision.Effects);
        Assert.IsFalse(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.IngressFailed, decision.WarningKind);
        Assert.AreSame(expected, decision.Exception);
        Assert.IsFalse(decision.LogUnsupportedFormats);
    }

    [TestMethod]
    public void Evaluate_ExtractionFailurePublishesNoneAndVisibleIngressWarning()
    {
        var expected = new InvalidOperationException("FileDrop extraction failed");
        var data = new TestDataObject(null, extractionFailure: expected);

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: false,
            _ => throw new AssertFailedException("Acquisition must not run."));

        Assert.AreEqual(DragDropEffects.None, decision.Effects);
        Assert.IsFalse(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.IngressFailed, decision.WarningKind);
        Assert.AreSame(expected, decision.Exception);
    }

    [TestMethod]
    public void Evaluate_QueueRejectionPublishesNoneAndQueueWarning()
    {
        var expected = new InvalidOperationException("queue stopped");
        var data = new TestDataObject(["external.bms"]);

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: false,
            _ => DroppedInstallIngressAcquisitionResult.Failure(
                DroppedInstallIngressFailureKind.QueueRejected,
                expected));

        Assert.AreEqual(DragDropEffects.None, decision.Effects);
        Assert.IsFalse(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.QueueUnavailable, decision.WarningKind);
        Assert.AreSame(expected, decision.Exception);
    }

    [TestMethod]
    public void Evaluate_PlaylistBlockDoesNotTouchExternalDataProvider()
    {
        var data = new TestDataObject(
            null,
            presenceFailure: new InvalidOperationException("must not be observed"));

        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            data,
            playlistDownloadBlocked: true,
            _ => throw new AssertFailedException("Acquisition must not run."));

        Assert.AreEqual(DragDropEffects.None, decision.Effects);
        Assert.IsFalse(decision.ExpandPendingTree);
        Assert.AreEqual(DroppedInstallDropWarningKind.PlaylistDownloadBlocked, decision.WarningKind);
        Assert.AreEqual(0, data.PresenceProbeCount);
    }

    private sealed class TestDataObject(
        string[]? paths,
        bool dataPresent = true,
        Exception? presenceFailure = null,
        Exception? extractionFailure = null) : IDataObject
    {
        internal int PresenceProbeCount { get; private set; }

        public object GetData(string format, bool autoConvert)
        {
            if (extractionFailure != null)
            {
                throw extractionFailure;
            }
            return paths ?? [];
        }

        public object GetData(string format) => GetData(format, autoConvert: true);

        public object GetData(Type format) => GetData(format?.FullName ?? string.Empty, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            PresenceProbeCount++;
            if (presenceFailure != null)
            {
                throw presenceFailure;
            }
            return dataPresent;
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

        public bool GetDataPresent(Type format) => GetDataPresent(format?.FullName ?? string.Empty, autoConvert: true);

        public string[] GetFormats(bool autoConvert) => dataPresent ? [DataFormats.FileDrop] : [];

        public string[] GetFormats() => GetFormats(autoConvert: true);

        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();

        public void SetData(string format, object data) => throw new NotSupportedException();

        public void SetData(Type format, object data) => throw new NotSupportedException();

        public void SetData(object data) => throw new NotSupportedException();
    }
}
