using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RankingCacheDownloadWorkflowOwnerTests
{
    [TestMethod]
    public void EmptyRequest_DoesNotQueryOrShowDialog()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target(" "), Target(""), Target("\t")]);

        CollectionAssert.AreEqual(Array.Empty<string>(), runtime.Events.ToArray());
        Assert.AreEqual(0, runtime.QueryCount);
        Assert.AreEqual(0, dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, dialogs.MessageRequests.Count);
    }

    [TestMethod]
    public void Request_NormalizesCaseInsensitiveHashesAndShowsNotFound()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target("A"), Target("a"), Target("B"), Target("")]);

        CollectionAssert.AreEqual(new[] { "A", "B" }, (System.Collections.ICollection)runtime.LastHashes);
        CollectionAssert.AreEqual(new[] { "query", "message" }, runtime.Events.ToArray());
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_ranking_cache_notfound, dialogs.MessageRequests[0].MessageBoxText);
    }

    [TestMethod]
    public void Candidates_ConfirmationIncludesCountsAndAcceptedDownloadPublishesSummary()
    {
        var runtime = new RecordingRuntime
        {
            Candidates = [CacheInfo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1024), CacheInfo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2048)],
            DownloadedFailures = [CacheInfo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2048)]
        };
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Target("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Target("cccccccccccccccccccccccccccccccc")]);

        CollectionAssert.AreEqual(new[] { "query", "confirm", "download", "message" }, runtime.Events.ToArray());
        Assert.AreEqual(1, runtime.DownloadCount);
        StringAssert.Contains(dialogs.ConfirmationRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Download + ": 2");
        StringAssert.Contains(dialogs.ConfirmationRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Skip + ": 1");
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Success + ": 1");
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Failure + ": 1");
    }

    [TestMethod]
    public void ConfirmationRejected_DoesNotDownload()
    {
        var runtime = new RecordingRuntime
        {
            Candidates = [CacheInfo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
        };
        var dialogs = new RecordingDialogService(runtime.Events)
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]);

        CollectionAssert.AreEqual(new[] { "query", "confirm" }, runtime.Events.ToArray());
        Assert.AreEqual(0, runtime.DownloadCount);
    }

    [TestMethod]
    public void RuntimeInvalidOperation_ShowsWarningWithoutLeakingDomainFailure()
    {
        var runtime = new RecordingRuntime
        {
            QueryFailure = new InvalidOperationException("score source unavailable")
        };
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]);

        CollectionAssert.AreEqual(new[] { "query", "message" }, runtime.Events.ToArray());
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_warn_cache_download, dialogs.MessageRequests[0].MessageBoxText);
    }

    [TestMethod]
    public void RuntimeUnexpectedFailure_ShowsErrorWithExceptionMessage()
    {
        var runtime = new RecordingRuntime
        {
            QueryFailure = new IOExceptionForTest("network failure")
        };
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.Request([Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]);

        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Msg_error_cache_download);
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, "network failure");
    }

    [TestMethod]
    public void ConfirmationDialogFailure_IsNotConvertedToWarningOrCancellation()
    {
        var runtime = new RecordingRuntime
        {
            Candidates = [CacheInfo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
        };
        var dialogs = new RecordingDialogService(runtime.Events)
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var owner = CreateOwner(runtime, dialogs);

        try
        {
            owner.Request([Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]);
            Assert.Fail("A failed confirmation dialog must fault the workflow.");
        }
        catch (InvalidOperationException)
        {
        }

        CollectionAssert.AreEqual(new[] { "query", "confirm" }, runtime.Events.ToArray());
        Assert.AreEqual(0, runtime.DownloadCount);
        Assert.AreEqual(0, dialogs.MessageRequests.Count);
    }

    [TestMethod]
    public void RequestCopiesInputBeforeDeferredExecution()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events);
        Action deferred = null!;
        var owner = new RankingCacheDownloadWorkflowOwner(
            runtime,
            new ChartFileOperationSynchronizer(),
            dialogs,
            action =>
            {
                deferred = action;
                return Task.CompletedTask;
            },
            (_, _) => { });
        var targets = new List<ChartOperationTarget> { Target("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") };

        owner.Request(targets);
        targets[0] = Target("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        deferred();

        CollectionAssert.AreEqual(new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, (System.Collections.ICollection)runtime.LastHashes);
    }

    [TestMethod]
    public void RankingAvailability_UsesTargetCapabilityAndCurrentLr2Id()
    {
        var runtime = new RecordingRuntime { CurrentLr2Id = 1234 };
        var owner = CreateOwner(runtime, new RecordingDialogService(runtime.Events));
        ChartOperationTarget rankingTarget = CreateTarget(
            ChartOperationCapabilities.UpdateRanking | ChartOperationCapabilities.UseLr2Ir,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartOperationTarget otherTarget = CreateTarget(ChartOperationCapabilities.OpenFile, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.IsTrue(owner.HasRankingTarget([rankingTarget, otherTarget]));
        Assert.IsTrue(owner.CanRequestRanking([rankingTarget]));

        runtime.CurrentLr2Id = 0;

        Assert.IsFalse(owner.CanRequestRanking([rankingTarget]));
        Assert.IsFalse(owner.HasRankingTarget([otherTarget]));
    }

    [TestMethod]
    public void RequestTargets_PreparesOnlyLr2IrHashesAsSnapshot()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events);
        Action deferred = null!;
        var owner = new RankingCacheDownloadWorkflowOwner(
            runtime,
            new ChartFileOperationSynchronizer(),
            dialogs,
            action =>
            {
                deferred = action;
                return Task.CompletedTask;
            },
            (_, _) => { });
        ChartOperationTarget rankingTarget = CreateTarget(
            ChartOperationCapabilities.UpdateRanking | ChartOperationCapabilities.UseLr2Ir,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartOperationTarget nonIrTarget = CreateTarget(ChartOperationCapabilities.UpdateRanking, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        owner.Request([rankingTarget, nonIrTarget]);
        deferred();

        CollectionAssert.AreEqual(new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, (System.Collections.ICollection)runtime.LastHashes);
    }

    private static ChartOperationTarget CreateTarget(ChartOperationCapabilities capabilities, string md5)
    {
        ChartFile chart = new ChartFile(
            ChartFileKind.Bms,
            "C:\\charts\\target.bms",
            md5,
            null,
            "title",
            "title",
            "artist",
            "genre",
            "folder",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private static ChartOperationTarget Target(string md5)
    {
        return CreateTarget(ChartOperationCapabilities.UseLr2Ir, md5);
    }

    private static RankingCacheDownloadWorkflowOwner CreateOwner(
        RecordingRuntime runtime,
        RecordingDialogService dialogs)
    {
        return new RankingCacheDownloadWorkflowOwner(
            runtime,
            new ChartFileOperationSynchronizer(),
            dialogs,
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            (_, _) => { });
    }

    private static BMSLibrary.IRDataCacheInfo CacheInfo(string md5, int size)
    {
        var json = new JObject
        {
            ["md5"] = md5,
            ["size"] = size,
            ["lastupdate"] = "2026-04-08 12:00:00"
        };
        return new BMSLibrary.IRDataCacheInfo(json);
    }

    private sealed class RecordingRuntime : IRankingCacheDownloadRuntime
    {
        internal int CurrentLr2Id { get; set; } = 1;

        internal List<string> Events { get; } = [];

        internal List<BMSLibrary.IRDataCacheInfo> Candidates { get; set; } = [];

        internal List<BMSLibrary.IRDataCacheInfo> DownloadedFailures { get; set; } = [];

        internal Exception QueryFailure { get; set; } = null!;

        internal int QueryCount { get; private set; }

        internal int DownloadCount { get; private set; }

        internal IReadOnlyList<string> LastHashes { get; private set; } = [];

        int IRankingCacheDownloadRuntime.CurrentLr2Id => CurrentLr2Id;

        public List<BMSLibrary.IRDataCacheInfo> GetIRDataNeedUpdates(IReadOnlyList<string> md5s)
        {
            QueryCount++;
            Events.Add("query");
            LastHashes = [.. md5s];
            if (QueryFailure != null)
            {
                throw QueryFailure;
            }

            return Candidates;
        }

        public List<BMSLibrary.IRDataCacheInfo> DownloadIRData(IReadOnlyList<BMSLibrary.IRDataCacheInfo> cacheInfo)
        {
            DownloadCount++;
            Events.Add("download");
            return DownloadedFailures;
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly List<string> events;

        internal RecordingDialogService(List<string> events)
        {
            this.events = events;
        }

        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("confirm");
            ConfirmationRequests.Add(request);
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class IOExceptionForTest : Exception
    {
        internal IOExceptionForTest(string message)
            : base(message)
        {
        }
    }
}
