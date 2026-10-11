using System;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateAvailableDialogViewModelTests
{
    /// <summary>
    /// メタデータ同梱版が先の入力でも通常版を初期選択し、入力配列順によって意図しない同梱資産を取得対象にする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Constructor_SelectsAppPackageEvenWhenMetadataAssetComesFirst()
    {
        var result = UpdateCheckResult.Available(
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

        using var progressHub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);

        Assert.AreEqual(2, viewModel.Packages.Count);
        Assert.AreEqual("app", viewModel.Packages[0].Asset.Kind);
        Assert.AreEqual("app", viewModel.SelectedAsset.Kind);
    }

    /// <summary>
    /// 起動処理の進行中だけ更新適用を禁止して理由を表示し、完了後の再受付と接続して起動との競合を許す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void StartupProgressFromProgressHubBlocksAndUnblocksApply()
    {
        var result = UpdateCheckResult.Available(
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
        using var progressHub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);

        Assert.IsTrue(viewModel.CanStartUpdate);
        progressHub.StartupProgress.ApplyPresentation(true, "startup", "", 0.0, 1.0);
        Assert.IsFalse(viewModel.CanStartUpdate);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_StartupBlocked, viewModel.StartBlockedReason);
        progressHub.StartupProgress.ApplyPresentation(false, null, null, 0.0, 1.0);
        Assert.IsTrue(viewModel.CanStartUpdate);
    }

    /// <summary>
    /// 破棄後は起動進捗の変更を通知せず、寿命が終わったダイアログへの購読が残る誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DisposeStopsListeningToProgressHub()
    {
        var result = UpdateCheckResult.NoUpdate("1.0.0.0");
        using var progressHub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);
        int notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;

        viewModel.Dispose();
        progressHub.StartupProgress.ApplyPresentation(true, "startup", "", 0.0, 1.0);

        Assert.AreEqual(0, notifications);
    }
}
