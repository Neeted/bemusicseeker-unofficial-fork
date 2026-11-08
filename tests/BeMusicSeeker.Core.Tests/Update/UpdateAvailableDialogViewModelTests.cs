using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateAvailableDialogViewModelTests
{
    /// <summary>
    /// 配信側が並べた順を保って先頭を初期選択し、名前と説明を同梱の有無から選ぶ。
    /// 入力順はファイル名順・同梱の有無の順のどちらとも異なるため、アプリ側でそれらの基準により並べ替えて
    /// 配信側の推奨と異なる資産を初期選択する誤りと、同梱版を通常版として表示する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Constructor_KeepsManifestOrderAndLabelsByMetadataFlag()
    {
        var result = UpdateCheckResult.Available(
            new Version(2, 2, 0, 0),
            "2.1.0.0",
            "2.2.0.0",
            [
                new UpdateAssetInfo
                {
                    FileName = "b-app.zip",
                    Url = "https://example.test/b-app.zip",
                    Sha256 = new string('a', 64),
                    SizeBytes = 1,
                    IncludesChartInfoMetadata = false
                },
                new UpdateAssetInfo
                {
                    FileName = "a-with-metadata.zip",
                    Url = "https://example.test/a-with-metadata.zip",
                    Sha256 = new string('b', 64),
                    SizeBytes = 2,
                    IncludesChartInfoMetadata = true
                },
                new UpdateAssetInfo
                {
                    FileName = "c-app.zip",
                    Url = "https://example.test/c-app.zip",
                    Sha256 = new string('c', 64),
                    SizeBytes = 3,
                    IncludesChartInfoMetadata = false
                }
            ],
            "https://example.test/releases/v2.2.0.0");

        using var progressHub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var viewModel = new UpdateAvailableDialogViewModel(result, progressHub);

        CollectionAssert.AreEqual(
            new[] { "b-app.zip", "a-with-metadata.zip", "c-app.zip" },
            viewModel.Packages.Select(package => package.FileName).ToArray());
        Assert.AreEqual("b-app.zip", viewModel.SelectedAsset.FileName);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_PackageAppOnly, viewModel.Packages[0].DisplayLabel);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_PackageAppOnlyDescription, viewModel.Packages[0].Description);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_PackageWithMetadata, viewModel.Packages[1].DisplayLabel);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.UpdateDialog_PackageWithMetadataDescription, viewModel.Packages[1].Description);
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
