using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OperationProgressHubViewModelTests
{
    [TestMethod]
    public void InstallPipelinePresentation_UsesPlaylistDropEstimateThenPendingPriority()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel();

        hub.UpdatePendingEstimateQueueStatus(new PendingInstallEstimateQueueStatusSnapshot
        {
            IsActive = true,
            PendingBatchCount = 2,
            CurrentPackageCount = 8,
            CompletedPackageCount = 3,
            CurrentDisplayName = "pending"
        });
        Assert.IsTrue(hub.IsInstallPipelineStatusActive);
        Assert.AreEqual(3, hub.InstallPipelineValue);

        hub.UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
        {
            IsActive = true,
            TotalWorkCount = 11,
            CompletedWorkCount = 4,
            CurrentDisplayName = "estimate"
        });
        Assert.AreEqual(4, hub.InstallPipelineValue);
        Assert.AreEqual(11, hub.InstallPipelineMaximum);

        hub.UpdateDropInstallQueueStatus(new DropInstallQueueStatusSnapshot
        {
            IsActive = true,
            TotalPathCount = 5,
            CompletedPathCount = 1,
            PendingBatchCount = 1,
            CanCancel = true,
            CurrentDisplayName = "drop"
        });
        Assert.AreEqual(1, hub.InstallPipelineValue);
        Assert.IsTrue(hub.InstallPipelineCanCancel);

        hub.UpdatePlaylistUrlDownloadStatus(new PlaylistUrlDownloadStatusSnapshot(
            isActive: true,
            totalCount: 9,
            completedCount: 2,
            currentDisplayName: "url",
            canCancel: true,
            labelFormat: "{0}/{1}"));
        Assert.AreEqual(2, hub.InstallPipelineValue);
        Assert.AreEqual(9, hub.InstallPipelineMaximum);
        Assert.AreEqual("url", hub.InstallPipelineSubLabel);

        hub.UpdatePlaylistUrlDownloadStatus(PlaylistUrlDownloadStatusSnapshot.Inactive);
        Assert.AreEqual(1, hub.InstallPipelineValue);
        Assert.AreEqual("drop", hub.InstallPipelineSubLabel);
    }

    [TestMethod]
    public void InstallPipelinePresentation_NormalizesEmptyMaximumAndInactiveState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel();

        hub.UpdateDropInstallQueueStatus(new DropInstallQueueStatusSnapshot
        {
            IsActive = true,
            TotalPathCount = 0,
            CompletedPathCount = 0
        });
        Assert.AreEqual(1, hub.InstallPipelineMaximum);

        hub.UpdateDropInstallQueueStatus(new DropInstallQueueStatusSnapshot());
        Assert.IsFalse(hub.IsInstallPipelineStatusActive);
        Assert.AreEqual(0, hub.InstallPipelineValue);
        Assert.AreEqual(1, hub.InstallPipelineMaximum);
    }

    [TestMethod]
    public void FolderAutoRenamePresentation_NormalizesProgressAndClearsOnCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel();

        hub.UpdateFolderAutoRenameProgress(new FolderAutoRenameProgressSnapshot
        {
            TotalCount = 0,
            ProcessedCount = 4,
            CurrentPath = "source"
        });

        Assert.IsTrue(hub.IsFolderAutoRenameProgressActive);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressMaximum);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressValue);
        StringAssert.Contains(hub.FolderAutoRenameProgressLabel, "1/1");
        Assert.AreEqual("source", hub.FolderAutoRenameProgressSubLabel);

        hub.UpdateFolderAutoRenameProgress(new FolderAutoRenameProgressSnapshot
        {
            TotalCount = 1,
            ProcessedCount = 1,
            CurrentPath = "source",
            IsCompleted = true
        });

        Assert.IsFalse(hub.IsFolderAutoRenameProgressActive);
        Assert.AreEqual(0.0, hub.FolderAutoRenameProgressValue);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressMaximum);
        Assert.AreEqual(string.Empty, hub.FolderAutoRenameProgressLabel);
        Assert.AreEqual(string.Empty, hub.FolderAutoRenameProgressSubLabel);
    }
}
