using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ScoreViewerRegistrationWorkflowOwnerTests
{
    [TestMethod]
    public void AppGatewayJsonParsers_PreserveStatusAndUploadContracts()
    {
        Assert.IsTrue(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"OK\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"REJECTED\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":null}"));
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{\"status\":1}"));

        ScoreViewerUploadResponse accepted = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"OK\",\"md5\":\"server-hash\"}");
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual("server-hash", accepted.Hash);
        Assert.AreEqual("OK", accepted.Status);

        ScoreViewerUploadResponse rejected = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"REJECTED\"}");
        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual("REJECTED", rejected.Status);
        Assert.IsNull(rejected.Hash);

        ScoreViewerUploadResponse nullStatus = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":null}");
        Assert.IsFalse(nullStatus.Accepted);
        Assert.AreEqual(string.Empty, nullStatus.Status);
    }

    [TestMethod]
    public void AppGatewayJsonParsers_RejectMissingPropertiesAndTrailingDocuments()
    {
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{}"));
        Assert.ThrowsException<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseUploadResponse("{\"status\":\"OK\"}"));
        Assert.ThrowsException<JsonReaderException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
                "{\"status\":\"OK\"} {\"status\":\"OK\"}"));
    }

    [TestMethod]
    public void SelectionQueries_UseScoreViewerCapabilityAndLocalFileAvailability()
    {
        string localPath = Path.GetTempFileName();
        try
        {
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                new RecordingGateway(),
                new RecordingInteraction());
            ChartOperationTarget localTarget = CreateTarget(
                "local",
                localPath,
                ChartOperationCapabilities.UseScoreViewer);
            ChartOperationTarget hashOnlyTarget = CreateTarget(
                "hash-only",
                null,
                ChartOperationCapabilities.UseScoreViewer);
            ChartOperationTarget unsupportedTarget = CreateTarget(
                "unsupported",
                localPath,
                ChartOperationCapabilities.None);

            Assert.IsTrue(owner.HasScoreViewerTarget([localTarget, unsupportedTarget]));
            Assert.IsTrue(owner.HasScoreViewerTarget([hashOnlyTarget]));
            Assert.IsFalse(owner.HasScoreViewerTarget([unsupportedTarget]));
            Assert.IsTrue(owner.CanRegisterScoreViewer([localTarget]));
            Assert.IsFalse(owner.CanRegisterScoreViewer([hashOnlyTarget]));
            Assert.IsFalse(owner.CanRegisterScoreViewer([unsupportedTarget]));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [TestMethod]
    public async Task RunAsync_ChartTargetsProjectsEffectiveSingleTargetAndOpensViewer()
    {
        var gateway = new RecordingGateway();
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction);

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [
                CreateTarget("ignored", null, ChartOperationCapabilities.None),
                CreateTarget("effective", null, ChartOperationCapabilities.UseScoreViewer),
                CreateTarget(null, null, ChartOperationCapabilities.UseScoreViewer)
            ],
            "chart_targets_single");

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("effective", result.Items[0].Hash);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.HashOnly, result.Items[0].Status);
        Assert.AreEqual(1, interaction.ConfirmationCount);
        Assert.AreEqual(result.LastViewUrl, interaction.OpenedUrl);
        Assert.AreEqual(0, gateway.StatusHashes.Count);
    }

    [TestMethod]
    public async Task RunAsync_ChartTargetsKeepsMultipleEffectiveTargetsWithoutAutoOpen()
    {
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
            new RecordingGateway(),
            interaction);

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [
                CreateTarget("first", null, ChartOperationCapabilities.UseScoreViewer),
                CreateTarget("ignored", null, ChartOperationCapabilities.None),
                CreateTarget("second", null, ChartOperationCapabilities.UseScoreViewer)
            ],
            "chart_targets_multiple");

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            result.Items.Select(item => item.Hash).ToArray());
        Assert.IsNull(interaction.OpenedUrl);
    }

    [TestMethod]
    public async Task RunAsync_HashOnlyTargetSkipsNetworkAndOpensSingleViewer()
    {
        var gateway = new RecordingGateway();
        var interaction = new RecordingInteraction();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction);
        var target = new ScoreViewerTarget("hash-only", path: null, "Hash only");

        ScoreViewerRegistrationResult result = await owner.RunAsync(
            [target],
            openSingleViewerOnSuccess: true,
            "hash_only");

        Assert.IsNotNull(result);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.HashOnly, result.Items.Single().Status);
        Assert.AreEqual("https://bms-score-viewer.pages.dev/view?md5=hash-only", result.LastViewUrl);
        Assert.AreEqual(0, gateway.StatusHashes.Count);
        Assert.AreEqual(0, gateway.UploadPaths.Count);
        Assert.AreEqual(1, interaction.ConfirmationCount);
        Assert.AreSame(result, interaction.PresentedResult);
        Assert.AreEqual(result.LastViewUrl, interaction.OpenedUrl);
    }

    [TestMethod]
    public async Task RunAsync_PreservesRegisteredStatusFailureAndDeclinedUpload()
    {
        string registeredPath = Path.GetTempFileName();
        string uploadPath = Path.GetTempFileName();
        string failedStatusPath = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway
            {
                RegistrationStatus = hash => hash switch
                {
                    "registered" => true,
                    "status-failure" => throw new IOException("status failed"),
                    _ => false,
                }
            };
            var interaction = new RecordingInteraction { ConfirmationResult = false };
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [
                    new ScoreViewerTarget("registered", registeredPath, "Registered"),
                    new ScoreViewerTarget("needs-upload", uploadPath, "Needs upload"),
                    new ScoreViewerTarget("status-failure", failedStatusPath, "Status failure")
                ],
                openSingleViewerOnSuccess: false,
                "mixed_preflight");

            CollectionAssert.AreEqual(
                new[]
                {
                    ScoreViewerRegistrationItemStatus.AlreadyRegistered,
                    ScoreViewerRegistrationItemStatus.UploadDeclined,
                    ScoreViewerRegistrationItemStatus.StatusCheckFailed
                },
                result.Items.Select(item => item.Status).ToArray());
            Assert.AreEqual(0, gateway.UploadPaths.Count);
            Assert.AreEqual(1, interaction.ConfirmationCount);
            Assert.IsTrue(result.HasFailures);
            Assert.IsTrue(result.HasUploadDeclined);
            Assert.AreEqual(1, warnings.Count(item => item.Message.Contains("score_viewer_status_failed")));
        }
        finally
        {
            File.Delete(registeredPath);
            File.Delete(uploadPath);
            File.Delete(failedStatusPath);
        }
    }

    [TestMethod]
    public async Task RunAsync_ReportsUploadSuccessRejectionAndExceptionPerTarget()
    {
        string successPath = Path.GetTempFileName();
        string rejectedPath = Path.GetTempFileName();
        string failedPath = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway
            {
                UploadResult = path => path == successPath
                    ? ScoreViewerUploadResponse.Success("server-hash")
                    : path == rejectedPath
                        ? ScoreViewerUploadResponse.Rejected("REJECTED")
                        : throw new IOException("upload failed")
            };
            var interaction = new RecordingInteraction();
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [
                    new ScoreViewerTarget("success", successPath, "Success"),
                    new ScoreViewerTarget("rejected", rejectedPath, "Rejected"),
                    new ScoreViewerTarget("failed", failedPath, "Failed")
                ],
                openSingleViewerOnSuccess: false,
                "upload_results");

            CollectionAssert.AreEqual(
                new[]
                {
                    ScoreViewerRegistrationItemStatus.Uploaded,
                    ScoreViewerRegistrationItemStatus.UploadFailed,
                    ScoreViewerRegistrationItemStatus.UploadFailed
                },
                result.Items.Select(item => item.Status).ToArray());
            Assert.AreEqual("server-hash", result.Items[0].Hash);
            Assert.AreEqual("Score Viewer upload status: REJECTED", result.Items[1].FailureMessage);
            Assert.AreEqual("upload failed", result.Items[2].FailureMessage);
            Assert.AreEqual(3, gateway.UploadPaths.Count);
            Assert.IsTrue(result.HasUploadedRegistration);
            Assert.IsTrue(result.HasFailures);
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_rejected")));
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_failed")));
        }
        finally
        {
            File.Delete(successPath);
            File.Delete(rejectedPath);
            File.Delete(failedPath);
        }
    }

    [TestMethod]
    public async Task RunAsync_ConfirmationFailureUsesFailurePresentationAndDoesNotUpload()
    {
        string path = Path.GetTempFileName();
        try
        {
            var gateway = new RecordingGateway();
            var interaction = new RecordingInteraction
            {
                ConfirmationException = new InvalidOperationException("confirmation failed")
            };
            var warnings = new List<(Exception? Exception, string Message)>();
            ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
                gateway,
                interaction,
                warnings.Add,
                showSingleTargetConfirmation: false);

            ScoreViewerRegistrationResult result = await owner.RunAsync(
                [new ScoreViewerTarget("target", path, "Target")],
                openSingleViewerOnSuccess: true,
                "confirmation_failure");

            Assert.IsNull(result);
            Assert.IsFalse(interaction.LastShowSingleTargetConfirmation);
            Assert.AreEqual("confirmation failed", interaction.PresentedFailure?.Message);
            Assert.AreEqual(0, gateway.UploadPaths.Count);
            Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_registration_failed")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ScoreViewerRegistrationWorkflowOwner CreateOwner(
        RecordingGateway gateway,
        RecordingInteraction interaction,
        Action<(Exception? Exception, string Message)>? warning = null,
        bool showSingleTargetConfirmation = true)
    {
        return new ScoreViewerRegistrationWorkflowOwner(
            gateway,
            interaction,
            () => showSingleTargetConfirmation,
            (exception, message) => warning?.Invoke((exception, message)));
    }

    private static ChartOperationTarget CreateTarget(
        string? md5,
        string? path,
        ChartOperationCapabilities capabilities)
    {
        ChartFile chart = new(
            ChartFileKind.Bms,
            path!,
            md5!,
            sha256: null,
            title: md5 ?? string.Empty,
            rawTitle: md5 ?? string.Empty,
            artist: string.Empty,
            genre: string.Empty,
            folder: string.Empty,
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: null,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private sealed class RecordingGateway : IScoreViewerRegistrationGateway
    {
        internal Func<string, bool> RegistrationStatus { get; set; } = _ => false;

        internal Func<string, ScoreViewerUploadResponse> UploadResult { get; set; } = _ => ScoreViewerUploadResponse.Success(string.Empty);

        internal List<string> StatusHashes { get; } = [];

        internal List<string> UploadPaths { get; } = [];

        public bool IsRegistered(string hash)
        {
            StatusHashes.Add(hash);
            return RegistrationStatus(hash);
        }

        public ScoreViewerUploadResponse Upload(string path)
        {
            UploadPaths.Add(path);
            return UploadResult(path);
        }
    }

    private sealed class RecordingInteraction : IScoreViewerRegistrationInteraction
    {
        internal bool ConfirmationResult { get; set; } = true;

        internal Exception? ConfirmationException { get; set; }

        internal int ConfirmationCount { get; private set; }

        internal bool LastShowSingleTargetConfirmation { get; private set; }

        internal ScoreViewerRegistrationResult? PresentedResult { get; private set; }

        internal Exception? PresentedFailure { get; private set; }

        internal string? OpenedUrl { get; private set; }

        public Task<bool> ConfirmUploadAsync(
            ScoreViewerRegistrationPlan plan,
            bool showSingleTargetConfirmation)
        {
            ConfirmationCount++;
            LastShowSingleTargetConfirmation = showSingleTargetConfirmation;
            return ConfirmationException == null
                ? Task.FromResult(ConfirmationResult)
                : Task.FromException<bool>(ConfirmationException);
        }

        public Task PresentResultAsync(ScoreViewerRegistrationResult result)
        {
            PresentedResult = result;
            return Task.CompletedTask;
        }

        public Task PresentFailureAsync(Exception exception)
        {
            PresentedFailure = exception;
            return Task.CompletedTask;
        }

        public void OpenViewer(string url)
        {
            OpenedUrl = url;
        }
    }
}
