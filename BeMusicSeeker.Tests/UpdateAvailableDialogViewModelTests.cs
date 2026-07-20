using System;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateAvailableDialogViewModelTests
{
    [TestMethod]
    public void Constructor_SelectsAppPackageEvenWhenMetadataAssetComesFirst()
    {
        UpdateCheckResult result = UpdateCheckResult.Available(
            new Version(2, 2, 0, 0),
            "2.1.0.0",
            "2.2.0.0",
            [
                new UpdateAssetInfo
                {
                    Kind = "app-with-metadata",
                    Label = "App with metadata bundle",
                    FileName = "bemusicseeker-unofficial-fork-v2.2.0.0-with-metadata.zip",
                    Url = "https://example.test/with-metadata.zip",
                    Sha256 = new string('b', 64),
                    SizeBytes = 2,
                    IncludesChartInfoMetadata = true
                },
                new UpdateAssetInfo
                {
                    Kind = "app",
                    Label = "App only",
                    FileName = "bemusicseeker-unofficial-fork-v2.2.0.0.zip",
                    Url = "https://example.test/app.zip",
                    Sha256 = new string('a', 64),
                    SizeBytes = 1,
                    IncludesChartInfoMetadata = false
                }
            ],
            "https://example.test/releases/v2.2.0.0");

        using var viewModel = new UpdateAvailableDialogViewModel(result, null);

        Assert.AreEqual(2, viewModel.Packages.Count);
        Assert.AreEqual("app", viewModel.Packages[0].Asset.Kind);
        Assert.AreEqual("app", viewModel.SelectedAsset.Kind);
    }

    [TestMethod]
    public void StartupProgressFromProgressHubBlocksAndUnblocksApply()
    {
        UpdateCheckResult result = UpdateCheckResult.Available(
            new Version(2, 2, 0, 0),
            "2.1.0.0",
            "2.2.0.0",
            [new UpdateAssetInfo
            {
                Kind = "app",
                Label = "App only",
                FileName = "app.zip",
                Url = "https://example.test/app.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 1,
                IncludesChartInfoMetadata = false
            }],
            "https://example.test/releases/v2.2.0.0");
        var progressHub = new OperationProgressHubViewModel();
        using var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);

        Assert.IsTrue(viewModel.CanStartUpdate);
        progressHub.IsStartupProgressActive = true;
        Assert.IsFalse(viewModel.CanStartUpdate);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_StartupBlocked, viewModel.StartBlockedReason);
        progressHub.IsStartupProgressActive = false;
        Assert.IsTrue(viewModel.CanStartUpdate);
    }

    [TestMethod]
    public void DisposeStopsListeningToProgressHub()
    {
        UpdateCheckResult result = UpdateCheckResult.NoUpdate("1.0.0.0");
        var progressHub = new OperationProgressHubViewModel();
        var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);
        int notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        viewModel.Dispose();
        progressHub.IsStartupProgressActive = true;

        Assert.AreEqual(0, notifications);
    }
}
