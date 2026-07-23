using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
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
    public void Execute_OpenLr2IrBuildsTrimmedMd5Url()
    {
        var urls = new List<string>();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(urlLauncher: urls.Add);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.UseLr2Ir,
            md5: "  ABCDEFABCDEFABCDEFABCDEFABCDEFAB  ");

        owner.Execute(target, SelectedChartExternalActionKind.OpenLr2Ir);

        CollectionAssert.AreEqual(
            new[] { "https://bms-ir.org/new/song?songmd5=ABCDEFABCDEFABCDEFABCDEFABCDEFAB&view=both" },
            urls);
    }

    [TestMethod]
    public void Execute_OpenRepositoriesUsesSha256AndChartInfoFallback()
    {
        var urls = new List<string>();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(urlLauncher: urls.Add);
        string chartInfoSha256 = new string('A', 64);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenRepositoryBySha256,
            chartInfo: new LR2SongDBExtended.chart_info { sha256 = chartInfoSha256 });

        owner.Execute(target, SelectedChartExternalActionKind.OpenMocha);
        owner.Execute(target, SelectedChartExternalActionKind.OpenMinIr);

        CollectionAssert.AreEqual(
            new[]
            {
                "https://mocha-repository.info/song.php?sha256=" + chartInfoSha256.ToLowerInvariant(),
                "https://www.gaftalk.com/minir/#/viewer/song/" + chartInfoSha256.ToLowerInvariant() + "/0"
            },
            urls);
    }

    [TestMethod]
    public void Execute_PlaylistStorageProjectionPreservesChartInfoSha256()
    {
        var urls = new List<string>();
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(urlLauncher: urls.Add);
        string chartInfoSha256 = new string('c', 64);
        var chartInfo = new LR2SongDBExtended.chart_info { sha256 = chartInfoSha256 };
        var storageOwner = new TestableBmsFile();
        storageOwner.Apply(@"C:\Songs\alpha.bms", new string('d', 32));
        ChartFile chart = ChartFileProjection.FromBmsMetadata(
            storageOwner.path,
            storageOwner.hash,
            null,
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            null,
            null,
            chartInfo);
        var entry = new TestablePlaylistEntry(storageOwner.hash);
        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            chart,
            entryChartInfo: chartInfo,
            resolvedChartRef: LibraryChartRef.FromBmsFile(storageOwner))
            .CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(
            row,
            ChartOperationSourceScope.PlaylistOwned,
            out ChartOperationTarget target));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));

        owner.Execute(target, SelectedChartExternalActionKind.OpenMocha);

        CollectionAssert.AreEqual(
            new[] { "https://mocha-repository.info/song.php?sha256=" + chartInfoSha256 },
            urls);
    }

    [TestMethod]
    public void Execute_UrlLauncherFailureIsNotSuppressed()
    {
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            urlLauncher: _ => throw new InvalidOperationException("browser failed"));
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenRepositoryBySha256,
            sha256: new string('b', 64));

        Assert.ThrowsException<InvalidOperationException>(() =>
            owner.Execute(target, SelectedChartExternalActionKind.OpenMocha));
    }

    [TestMethod]
    public void Execute_InvalidRepositoryHashDoesNotOpenUrl()
    {
        int launcherCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(urlLauncher: _ => launcherCalls++);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenRepositoryBySha256,
            sha256: "not-a-sha256");

        owner.Execute(target, SelectedChartExternalActionKind.OpenMocha);

        Assert.AreEqual(0, launcherCalls);
    }

    [TestMethod]
    public void CanExecute_UsesExistingEligibilityWithoutInvokingLaunchers()
    {
        int launchCalls = 0;
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(
            fileExists: _ => true,
            explorerOpen: _ =>
            {
                launchCalls++;
                return new ExplorerOpenResult();
            },
            associatedFileLauncher: _ => launchCalls++,
            urlLauncher: _ => launchCalls++);
        ChartOperationTarget target = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.OpenFolder
                | ChartOperationCapabilities.OpenFile
                | ChartOperationCapabilities.UseLr2Ir
                | ChartOperationCapabilities.OpenRepositoryBySha256,
            md5: new string('a', 32),
            sha256: new string('b', 64));

        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenExplorer));
        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenFile));
        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenLr2Ir));
        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenMocha));
        Assert.IsTrue(owner.CanExecute(target, SelectedChartExternalActionKind.OpenMinIr));
        Assert.AreEqual(0, launchCalls);
    }

    [TestMethod]
    public void CanExecute_RejectsMissingPathCapabilityAndMalformedIdentifiers()
    {
        SelectedChartExternalActionWorkflowOwner owner = CreateOwner(fileExists: _ => false);
        ChartOperationTarget noCapability = CreateTarget(
            @"C:\Songs\alpha.bms",
            ChartOperationCapabilities.None,
            md5: new string('a', 32),
            sha256: new string('b', 64));
        ChartOperationTarget invalidMd5 = CreateTarget(
            @"C:\Songs\beta.bms",
            ChartOperationCapabilities.UseLr2Ir,
            md5: "not-md5");
        ChartOperationTarget invalidSha256 = CreateTarget(
            @"C:\Songs\gamma.bms",
            ChartOperationCapabilities.OpenRepositoryBySha256,
            sha256: "not-sha256");
        ChartOperationTarget fallbackSha256 = CreateTarget(
            @"C:\Songs\delta.bms",
            ChartOperationCapabilities.OpenRepositoryBySha256,
            chartInfo: new LR2SongDBExtended.chart_info { sha256 = new string('c', 64) });

        Assert.IsFalse(owner.CanExecute(noCapability, SelectedChartExternalActionKind.OpenExplorer));
        Assert.IsFalse(owner.CanExecute(noCapability, SelectedChartExternalActionKind.OpenFile));
        Assert.IsFalse(owner.CanExecute(invalidMd5, SelectedChartExternalActionKind.OpenLr2Ir));
        Assert.IsFalse(owner.CanExecute(invalidSha256, SelectedChartExternalActionKind.OpenMocha));
        Assert.IsTrue(owner.CanExecute(fallbackSha256, SelectedChartExternalActionKind.OpenMinIr));
    }

    private static SelectedChartExternalActionWorkflowOwner CreateOwner(
        Func<string, bool>? fileExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null,
        Action<string>? associatedFileLauncher = null,
        Action<string>? urlLauncher = null)
    {
        return new SelectedChartExternalActionWorkflowOwner(
            fileExists ?? (_ => true),
            explorerOpen ?? (_ => new ExplorerOpenResult()),
            associatedFileLauncher ?? (_ => { }),
            urlLauncher ?? (_ => { }));
    }

    private static ChartOperationTarget CreateTarget(
        string path,
        ChartOperationCapabilities capabilities,
        string? md5 = null,
        string? sha256 = null,
        LR2SongDBExtended.chart_info? chartInfo = null)
    {
        var file = new BMSFile { path = path };
        var chart = new ChartFile(
            ChartFileKind.Bms,
            path,
            md5,
            sha256,
            "Title",
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            string.Empty,
            null,
            null,
            chartInfo,
            file,
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

    private sealed class TestableBmsFile : BMSFile
    {
        internal void Apply(string filePath, string md5)
        {
            path = filePath;
            hash = md5;
        }
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
