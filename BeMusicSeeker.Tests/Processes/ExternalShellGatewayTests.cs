using System;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalShellGatewayTests
{
    /// <summary>空URLと空白ファイル対象を引数失敗として拒否し、曖昧なshell要求を作りません。</summary>
    [TestMethod]
    public void RequestFactoriesRejectEmptyTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => ExternalShellRequest.OpenUrl(string.Empty));
        Assert.ThrowsException<ArgumentException>(() => ExternalShellRequest.OpenAssociatedFile("  "));
    }

    /// <summary>関連文書の操作を注入gatewayへ関連付けファイル要求として渡し、対象pathや操作種別を取り違えません。</summary>
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
