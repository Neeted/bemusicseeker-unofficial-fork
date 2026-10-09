using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartExternalActionWorkflowOwnerTests
{
    [TestMethod]
    public void Execute_OpenExplorerPassesExistingPathToGateway()
    {
        var explorerPaths = new List<string>();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            explorerOpen: path =>
            {
                explorerPaths.Add(path);
                return new ExplorerOpenResult { Kind = ExplorerOpenResultKind.SelectedFile };
            });
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFolder);

        owner.Execute(target, SelectedChartExternalActionKind.OpenExplorer);

        CollectionAssert.AreEqual(new[] { @"C:\Songs\alpha.bms" }, explorerPaths);
    }

    [TestMethod]
    public void Execute_OpenFileSuppressesAssociatedLauncherFailure()
    {
        int launcherCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            associatedFileLauncher: _ =>
            {
                launcherCalls++;
                throw new InvalidOperationException("association failed");
            });
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFile);

        owner.Execute(target, SelectedChartExternalActionKind.OpenFile);

        Assert.AreEqual(1, launcherCalls);
    }

    [TestMethod]
    public void Execute_MissingPathOrCapabilityDoesNotOpenAnything()
    {
        int gatewayCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: _ => false,
            explorerOpen: _ =>
            {
                gatewayCalls++;
                return new ExplorerOpenResult();
            },
            associatedFileLauncher: _ => gatewayCalls++);
        ChartOperationTarget noCapability = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.None);
        ChartOperationTarget missingPath = CreateTarget(
            @"C:\Songs\beta.bms",
            ChartOperationCapabilities.OpenFile);

        owner.Execute(noCapability, SelectedChartExternalActionKind.OpenExplorer);
        owner.Execute(missingPath, SelectedChartExternalActionKind.OpenFile);

        Assert.AreEqual(0, gatewayCalls);
    }

    [TestMethod]
    public void ResolveConfiguredActions_PlaylistStorageProjectionPreservesChartInfoSha256()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            });
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(settingsProvider: () => settings);
        string chartInfoSha256 = new string('c', 64);
        var chartInfo = new BeMusicSeeker.Models.ChartDetails { sha256 = chartInfoSha256 };
        ChartFile storageOwner = ChartTestValues.Empty();
        storageOwner = storageOwner with { Path = @"C:\Songs\alpha.bms", Md5 = new string('d', 32) };
        ChartFile chart = ChartFileProjection.FromBmsMetadata(
            storageOwner.Path,
            storageOwner.Md5,
            null,
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            null,
            null,
            chartInfo);
        var entry = new TestablePlaylistEntry(storageOwner.Md5);
        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            chart,
            entryChartInfo: chartInfo,
            resolvedChartRef: LibraryChartRef.FromChartFile((storageOwner)))
            .CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(
            row,
            ChartOperationSourceScope.PlaylistOwned,
            out ChartOperationTarget target));
        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        Assert.AreEqual(chartInfoSha256, input.Sha256);

        RightClickActionResolution resolution = owner.ResolveConfiguredActions(input);
        ResolvedRightClickWebAction kaleid = resolution.WebActions.Single(action =>
            action.Id == "kaleid-ir");
        Assert.AreEqual("https://kaleidir.com/charts/" + chartInfoSha256, kaleid.Url);
    }

    [TestMethod]
    public void Execute_UrlLauncherFailureReturnsTypedResult()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            });
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            urlLauncher: _ => throw new InvalidOperationException("browser failed"),
            settingsProvider: () => settings);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.None,
            sha256: new string('b', 64));

        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        ExternalConfiguredActionResult result = owner.ExecuteConfiguredAction(
            input,
            ConfiguredExternalActionKind.Web,
            "mocha");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExternalConfiguredActionFailureKind.WebLaunchFailed, result.FailureKind);
        Assert.IsInstanceOfType(result.Exception, typeof(InvalidOperationException));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Execute_ConfiguredProgramRechecksActionAndPassesResolvedTokens(bool bmson, bool pending)
    {
        ChartFileKind kind = bmson ? ChartFileKind.Bmson : ChartFileKind.Bms;
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings(
                [],
                [new RightClickProgramActionDefinition(
                    "player",
                    "Player",
                    @"C:\Tools\player.exe",
                    "--chart \"{filePath}\"",
                    enabled: true)]));
        TestExternalProgramLaunchGateway programGateway = new();
        string chartPath = @"C:\Songs\folder name\alpha." + (kind == ChartFileKind.Bmson ? "bmson" : "bms");
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: path => path == chartPath,
            settingsProvider: () => settings,
            externalProgramLaunchGateway: programGateway);
        ChartFile chart = CreateChart(chartPath, kind);
        LibraryChartRow row = pending
            ? LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart))
            : LibraryChartRow.FromChartFile(chart);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, pending, out ChartOperationTarget target));
        Assert.AreEqual(!pending, target.IsOwned);
        Assert.AreEqual(pending, target.IsPending);
        Assert.AreSame(row.PackageEntry, target.PackageEntry);

        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        RightClickActionResolution resolution = owner.ResolveConfiguredActions(input);
        Assert.AreEqual(1, resolution.ProgramActions.Count);
        CollectionAssert.AreEqual(
            new[] { "--chart", chartPath },
            resolution.ProgramActions[0].Arguments.ToArray());

        ExternalConfiguredActionResult result = owner.ExecuteConfiguredAction(
            input,
            ConfiguredExternalActionKind.Program,
            "player");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(programGateway.Request);
        Assert.AreEqual(@"C:\Tools\player.exe", programGateway.Request.ExecutablePath);
        Assert.AreEqual(chartPath, programGateway.Request.ChartFilePath);
        CollectionAssert.AreEqual(
            new[] { "--chart", chartPath },
            programGateway.Request.Arguments.ToArray());

        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings([], []));
        ExternalConfiguredActionResult unavailable = owner.ExecuteConfiguredAction(
            input, ConfiguredExternalActionKind.Program, "player");

        Assert.AreEqual(ExternalConfiguredActionFailureKind.ActionUnavailable, unavailable.FailureKind);
        Assert.AreEqual(1, programGateway.RequestCount);
    }

    [TestMethod]
    [DataRow("playlist-missing")]
    [DataRow("hash-only")]
    [DataRow("missing-pending-file")]
    [DataRow("no-open-file-capability")]
    public void ResolveConfiguredProgram_RejectsUnavailableFileTargetsWithoutLaunching(string condition)
    {
        const string chartPath = @"C:\Songs\alpha.bms";
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
                new RightClickActionSettings([], [new RightClickProgramActionDefinition(
                    "player", "Player", @"C:\Tools\player.exe", "{filePath}", enabled: true)]));
            });
        TestExternalProgramLaunchGateway programGateway = new();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: path => path == chartPath && condition != "missing-pending-file",
            settingsProvider: () => settings,
            externalProgramLaunchGateway: programGateway);
        ChartFile chart = CreateChart(condition == "hash-only" ? null : chartPath, ChartFileKind.Bms);
        ChartOperationTarget target;
        if (condition == "playlist-missing")
        {
            ChartFile missingChart = CreateChart(chartPath, ChartFileKind.Bms, md5: null);
            var entry = new TestablePlaylistEntry(new string('d', 32));
            PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, missingChart).CreateViewRow();
            Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out target));
            Assert.IsTrue(target.IsPlaylistMissing);
        }
        else if (condition == "no-open-file-capability")
        {
            target = CreateTarget(chartPath, ChartOperationCapabilities.None);
        }
        else
        {
            var row = LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart));
            Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, isPendingSection: true, out target));
        }

        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        Assert.IsNull(input.LocalFilePath);
        Assert.AreEqual(0, owner.ResolveConfiguredActions(input).ProgramActions.Count);
        ExternalConfiguredActionResult result = owner.ExecuteConfiguredAction(
            input, ConfiguredExternalActionKind.Program, "player");
        Assert.AreEqual(ExternalConfiguredActionFailureKind.ActionUnavailable, result.FailureKind);
        Assert.AreEqual(0, programGateway.RequestCount);
    }

    [TestMethod]
    public void Execute_ConfiguredProgramMapsGatewayMissingExecutableToTypedFailure()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings(
                [],
                [new RightClickProgramActionDefinition(
                    "player",
                    "Player",
                    @"C:\Tools\player.exe",
                    "{filePath}",
                    enabled: true)]));
        TestExternalProgramLaunchGateway programGateway = new()
        {
            Result = ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.MissingExecutable,
                "raw executable detail")
        };
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            settingsProvider: () => settings,
            externalProgramLaunchGateway: programGateway);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFile);

        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        ExternalConfiguredActionResult result = owner.ExecuteConfiguredAction(
            input,
            ConfiguredExternalActionKind.Program,
            "player");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExternalConfiguredActionFailureKind.ProgramExecutableMissing, result.FailureKind);
        Assert.AreEqual("raw executable detail", result.Diagnostic);
    }

    [TestMethod]
    public void Execute_InvalidSettingsReturnsTypedFailureWithInternalDiagnosticOnly()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.RightClickActionsJson = "{invalid";
            });
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            settingsProvider: () => settings);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFile);

        Assert.IsTrue(owner.TryCreateResolutionInput(target, out RightClickActionResolutionInput input));
        ExternalConfiguredActionResult result = owner.ExecuteConfiguredAction(
            input,
            ConfiguredExternalActionKind.Web,
            "bms-ir");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ExternalConfiguredActionFailureKind.InvalidSettings, result.FailureKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    [TestMethod]
    public void CanExecute_UsesExistingLocalEligibilityWithoutInvokingLaunchers()
    {
        int launchCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: _ => true,
            explorerOpen: _ =>
            {
                launchCalls++;
                return new ExplorerOpenResult();
            },
            associatedFileLauncher: _ => launchCalls++);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFolder | ChartOperationCapabilities.OpenFile);

        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenExplorer));
        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenFile));
        Assert.AreEqual(0, launchCalls);
    }

    [TestMethod]
    public void CanExecute_RejectsMissingLocalPathCapability()
    {
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(fileExists: _ => false);
        ChartOperationTarget noCapability = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.None);
        ChartOperationTarget missingFile = CreateTarget(
            @"C:\Songs\beta.bms",
            ChartOperationCapabilities.OpenFolder | ChartOperationCapabilities.OpenFile);

        Assert.IsFalse(owner.CanExecute(noCapability, SelectedChartExternalActionKind.OpenExplorer));
        Assert.IsFalse(owner.CanExecute(noCapability, SelectedChartExternalActionKind.OpenFile));
        Assert.IsFalse(owner.CanExecute(missingFile, SelectedChartExternalActionKind.OpenExplorer));
        Assert.IsFalse(owner.CanExecute(missingFile, SelectedChartExternalActionKind.OpenFile));
    }

    [TestMethod]
    public async Task QueryRelatedDocuments_ReturnsTextBeforeHtmlCandidates()
    {
        var requests = new List<string>();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            directoryNameResolver: _ => @"C:\Songs",
            relatedDocumentFileEnumerator: (_, pattern) =>
            {
                requests.Add(pattern);
                return pattern == "*.txt"
                    ? [@"C:\Songs\readme.txt"]
                    : [@"C:\Songs\manual.html"];
            });

        RelatedDocumentQueryReceipt receipt = await owner.QueryRelatedDocumentsAsync(
            CreateTarget(@"C:\Songs\alpha.bms", ChartOperationCapabilities.None),
            CancellationToken.None);

        Assert.AreEqual(RelatedDocumentQueryStatus.Available, receipt.Status);
        CollectionAssert.AreEqual(
            new[] { @"C:\Songs\readme.txt", @"C:\Songs\manual.html" },
            receipt.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { "*.txt", "*.htm?" }, requests);
    }

    [TestMethod]
    public async Task QueryRelatedDocuments_MissingChartPathDoesNotEnumerate()
    {
        int enumerationCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: _ => false,
            relatedDocumentFileEnumerator: (_, _) =>
            {
                enumerationCalls++;
                return [];
            });

        RelatedDocumentQueryReceipt receipt = await owner.QueryRelatedDocumentsAsync(
            CreateTarget(@"C:\Songs\missing.bms", ChartOperationCapabilities.None),
            CancellationToken.None);

        Assert.AreEqual(RelatedDocumentQueryStatus.Unavailable, receipt.Status);
        Assert.AreEqual(0, enumerationCalls);
    }

    [TestMethod]
    public async Task QueryRelatedDocuments_DistinguishesEmptyAndEnumerationFailure()
    {
        SelectedChartExternalActionWorkflowOwner emptyOwner = CreateOwner(
            relatedDocumentFileEnumerator: (_, _) => []);
        SelectedChartExternalActionWorkflowOwner failedOwner = CreateOwner(
            relatedDocumentFileEnumerator: (_, _) => throw new IOException("enumeration failed"));
        ChartOperationTarget target = CreateTarget(@"C:\Songs\alpha.bms", ChartOperationCapabilities.None);

        RelatedDocumentQueryReceipt empty = await emptyOwner.QueryRelatedDocumentsAsync(target, CancellationToken.None);
        RelatedDocumentQueryReceipt failed = await failedOwner.QueryRelatedDocumentsAsync(target, CancellationToken.None);

        Assert.AreEqual(RelatedDocumentQueryStatus.Empty, empty.Status);
        Assert.AreEqual(RelatedDocumentQueryStatus.Failed, failed.Status);
    }

    [TestMethod]
    public void RelatedDocumentQueryReceipt_CopiesAvailablePaths()
    {
        var mutablePaths = new List<string> { @"C:\Songs\readme.txt" };

        var receipt = RelatedDocumentQueryReceipt.Available(mutablePaths);
        mutablePaths[0] = @"C:\Songs\changed.txt";

        CollectionAssert.AreEqual(
            new[] { @"C:\Songs\readme.txt" },
            receipt.Paths.ToArray());
    }

    [TestMethod]
    public async Task QueryRelatedDocuments_CancellationDoesNotReturnSuccessfulReceipt()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            relatedDocumentFileEnumerator: (_, _) =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return [];
            });
        ChartOperationTarget target = CreateTarget(@"C:\Songs\alpha.bms", ChartOperationCapabilities.None);

        using var cancellation = new CancellationTokenSource();
        Task<RelatedDocumentQueryReceipt> query = owner.QueryRelatedDocumentsAsync(target, cancellation.Token);
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, query, "related-documents.enumeration");
            cancellation.Cancel();
            release.TrySetResult(true);
            RelatedDocumentQueryReceipt receipt = await query;
            Assert.AreEqual(RelatedDocumentQueryStatus.Canceled, receipt.Status);
        }
        finally
        {
            release.TrySetResult(true);
            await query;
        }
    }

    [TestMethod]
    public void OpenRelatedDocument_RechecksPathAndPropagatesLauncherFailure()
    {
        int launcherCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
            associatedFileLauncher: path => launcherCalls++);

        owner.OpenRelatedDocument(@"C:\Songs\readme.txt");
        owner.OpenRelatedDocument(@"C:\Songs\missing.html");

        Assert.AreEqual(1, launcherCalls);

        SelectedChartExternalActionWorkflowOwner failingOwner = CreateOwner(
            associatedFileLauncher: _ => throw new InvalidOperationException("document launcher failed"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            failingOwner.OpenRelatedDocument(@"C:\Songs\readme.txt"));
    }

    private static SelectedChartExternalActionWorkflowOwner CreateOwner(
        Func<string, bool>? fileExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null,
        Action<string>? associatedFileLauncher = null,
        Action<string>? urlLauncher = null,
        Func<string, string>? directoryNameResolver = null,
        Func<string, string, IEnumerable<string>>? relatedDocumentFileEnumerator = null,
        Func<BeMusicSeeker.Properties.Settings>? settingsProvider = null,
        IExternalProgramLaunchGateway? externalProgramLaunchGateway = null)
    {
        var gateway = new TestExternalShellGateway(
            explorerOpen ?? (_ => new ExplorerOpenResult()),
            associatedFileLauncher ?? (_ => { }),
            urlLauncher ?? (_ => { }));
        return new SelectedChartExternalActionWorkflowOwner(
            fileExists ?? (_ => true),
            gateway,
            directoryNameResolver,
            relatedDocumentFileEnumerator,
            settingsProvider,
            externalProgramLaunchGateway);
    }

    private sealed class TestExternalShellGateway : IExternalShellGateway
    {
        private readonly Func<string, ExplorerOpenResult> explorerOpen;
        private readonly Action<string> associatedFileLauncher;
        private readonly Action<string> urlLauncher;

        internal TestExternalShellGateway(
            Func<string, ExplorerOpenResult> explorerOpen,
            Action<string> associatedFileLauncher,
            Action<string> urlLauncher)
        {
            this.explorerOpen = explorerOpen;
            this.associatedFileLauncher = associatedFileLauncher;
            this.urlLauncher = urlLauncher;
        }

        public void Open(ExternalShellRequest request)
        {
            if (request.Kind == ExternalShellRequestKind.Url)
            {
                urlLauncher(request.Target);
            }
            else
            {
                associatedFileLauncher(request.Target);
            }
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => explorerOpen(filePath);

        public ExplorerOpenResult OpenDirectory(string directoryPath) => new();

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }

    private sealed class TestExternalProgramLaunchGateway : IExternalProgramLaunchGateway
    {
        internal ExternalProgramLaunchRequest? Request { get; private set; }

        internal int RequestCount { get; private set; }

        internal ExternalProgramLaunchResult Result { get; set; } = ExternalProgramLaunchResult.Success;

        public ExternalProgramLaunchResult Launch(ExternalProgramLaunchRequest request)
        {
            Request = request;
            RequestCount++;
            return Result;
        }
    }

    private static ChartFile CreateChart(string? path, ChartFileKind kind, string? md5 = "dddddddddddddddddddddddddddddddd")
    {
        return new ChartFile(kind, path, md5, new string('a', 64), "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private static ChartOperationTarget CreateTarget(
        string path,
        ChartOperationCapabilities capabilities,
        string? md5 = null,
        string? sha256 = null,
        BeMusicSeeker.Models.ChartDetails? chartInfo = null)
    {
        ChartFile file = (ChartTestValues.Empty() with { Path = path });
        var chart = new ChartFile(ChartFileKind.Bms, path, md5, sha256, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, chartInfo);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }


    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        internal TestablePlaylistEntry(string md5Value)
        {
            md5 = md5Value;
            title = "Title";
            artist = "Artist";
        }
    }
}
