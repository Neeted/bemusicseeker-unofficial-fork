using System;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalShellGatewayTests
{
    [TestMethod]
    public void RequestFactoriesPreserveTypedOperationAndTarget()
    {
        ExternalShellRequest url = ExternalShellRequest.OpenUrl("https://example.invalid");
        ExternalShellRequest file = ExternalShellRequest.OpenAssociatedFile(@"C:\Songs\chart.bms");

        Assert.AreEqual(ExternalShellRequestKind.Url, url.Kind);
        Assert.AreEqual("https://example.invalid", url.Target);
        Assert.AreEqual(ExternalShellRequestKind.AssociatedFile, file.Kind);
        Assert.AreEqual(@"C:\Songs\chart.bms", file.Target);
    }

    [TestMethod]
    public void RequestFactoriesRejectEmptyTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => ExternalShellRequest.OpenUrl(string.Empty));
        Assert.ThrowsException<ArgumentException>(() => ExternalShellRequest.OpenAssociatedFile("  "));
    }

    [TestMethod]
    public void SelectedChartRelatedDocumentUsesInjectedGateway()
    {
        var gateway = new RecordingExternalShellGateway();
        var owner = new SelectedChartExternalActionWorkflowOwner(
            _ => true,
            gateway);

        owner.OpenRelatedDocument(@"C:\Songs\readme.txt");

        ExternalShellRequest request = gateway.LastRequest
            ?? throw new AssertFailedException("The external shell gateway did not receive a request.");
        Assert.AreEqual(ExternalShellRequestKind.AssociatedFile, request.Kind);
        Assert.AreEqual(@"C:\Songs\readme.txt", request.Target);
    }

    private sealed class RecordingExternalShellGateway : IExternalShellGateway
    {
        internal ExternalShellRequest? LastRequest { get; private set; }

        public void Open(ExternalShellRequest request)
        {
            LastRequest = request;
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => new();

        public ExplorerOpenResult OpenDirectory(string directoryPath) => new();

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }
}
