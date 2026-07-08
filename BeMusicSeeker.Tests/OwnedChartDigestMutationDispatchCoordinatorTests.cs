using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartDigestMutationDispatchCoordinatorTests
{
    [TestMethod]
    public void DispatchDigestChanges_BuildsDigestPlanAndDispatches()
    {
        var host = new CapturingHost();
        var coordinator = new OwnedChartDigestMutationDispatchCoordinator(host);
        var bmsChange = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            @"C:\Library\bms.bms",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            null);
        var bmsonChange = new LibraryChartDigestChange(
            LibraryChartKind.Bmson,
            @"C:\Library\bmson.bmson",
            "cccccccccccccccccccccccccccccccc",
            "old-sha",
            "cccccccccccccccccccccccccccccccc",
            "new-sha");

        coordinator.DispatchDigestChanges([bmsChange, bmsonChange], "chart_info_backfill_digest");

        Assert.AreEqual("chart_info_backfill_digest", host.LastReason);
        Assert.IsNotNull(host.LastPlan);
        Assert.AreEqual(2, host.LastPlan.DigestChanges.Count);
        Assert.IsFalse(host.LastPlan.RequiresInstalledLookupFullInvalidate);
        Assert.IsTrue(host.LastPlan.InstallEstimationMetadataProfileCacheInvalidated);
        Assert.IsTrue(host.LastPlan.DuplicateCacheInvalidated);
        Assert.IsTrue(host.LastPlan.PlaylistSummaryOwnedHashInvalidated);
        Assert.IsTrue(host.LastPlan.OwnedCollectionChanged);
        Assert.IsTrue(host.LastPlan.ResourceHealthIndexInvalidated);
        Assert.IsTrue(host.LastPlan.WarningPresentationChanged);
        Assert.IsTrue(host.LastPlan.BmsFilesStorageRowsChanged);
        Assert.IsTrue(host.LastPlan.BmsonSongsStorageRowsChanged);
    }

    [TestMethod]
    public void BuildDigestMutationPlan_ShaOnlyChangeDoesNotInvalidateResourceHealth()
    {
        var change = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            @"C:\Library\sha-only.bms",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "old-sha",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "new-sha");

        OwnedChartDigestMutationPlan plan = OwnedChartDigestMutationDispatchCoordinator.BuildDigestMutationPlan([change]);

        Assert.AreEqual(1, plan.DigestChanges.Count);
        Assert.IsTrue(plan.InstallEstimationMetadataProfileCacheInvalidated);
        Assert.IsFalse(plan.DuplicateCacheInvalidated);
        Assert.IsTrue(plan.PlaylistSummaryOwnedHashInvalidated);
        Assert.IsTrue(plan.OwnedCollectionChanged);
        Assert.IsFalse(plan.ResourceHealthIndexInvalidated);
        Assert.IsFalse(plan.WarningPresentationChanged);
        Assert.IsTrue(plan.BmsFilesStorageRowsChanged);
        Assert.IsFalse(plan.BmsonSongsStorageRowsChanged);
    }

    [TestMethod]
    public void BuildPotentialDigestMutationPlan_EmptyTargetsReturnsEmptyPlan()
    {
        OwnedChartDigestMutationPlan plan = OwnedChartDigestMutationDispatchCoordinator.BuildPotentialDigestMutationPlan([]);

        Assert.AreEqual(0, plan.DigestChanges.Count);
        Assert.IsFalse(plan.RequiresInstalledLookupFullInvalidate);
        Assert.IsFalse(plan.InstallEstimationMetadataProfileCacheInvalidated);
        Assert.IsFalse(plan.OwnedCollectionChanged);
        Assert.IsFalse(plan.ResourceHealthIndexInvalidated);
        Assert.IsFalse(plan.WarningPresentationChanged);
    }

    [TestMethod]
    public void DispatchPotentialDigestChanges_BuildsFullInvalidatePlan()
    {
        var host = new CapturingHost();
        var coordinator = new OwnedChartDigestMutationDispatchCoordinator(host);
        TestableBmsFile bmsFile = CreateBmsFile(@"C:\Library\bms.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\bmson.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        coordinator.DispatchPotentialDigestChanges(
            [
                ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false),
                ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false)
            ],
            "chart_info_backfill_digest_failed");

        Assert.AreEqual("chart_info_backfill_digest_failed", host.LastReason);
        Assert.IsNotNull(host.LastPlan);
        Assert.AreEqual(0, host.LastPlan.DigestChanges.Count);
        Assert.IsTrue(host.LastPlan.RequiresInstalledLookupFullInvalidate);
        Assert.IsTrue(host.LastPlan.InstallEstimationMetadataProfileCacheInvalidated);
        Assert.IsTrue(host.LastPlan.DuplicateCacheInvalidated);
        Assert.IsTrue(host.LastPlan.PlaylistSummaryOwnedHashInvalidated);
        Assert.IsTrue(host.LastPlan.OwnedCollectionChanged);
        Assert.IsTrue(host.LastPlan.ResourceHealthIndexInvalidated);
        Assert.IsTrue(host.LastPlan.WarningPresentationChanged);
        Assert.IsTrue(host.LastPlan.BmsFilesStorageRowsChanged);
        Assert.IsTrue(host.LastPlan.BmsonSongsStorageRowsChanged);
    }

    [TestMethod]
    public void BuildPotentialDigestMutationPlan_ResourceHealthFlagSuppressesWarningRefresh()
    {
        TestableBmsFile bmsFile = CreateBmsFile(@"C:\Library\bms.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        OwnedChartDigestMutationPlan plan = OwnedChartDigestMutationDispatchCoordinator.BuildPotentialDigestMutationPlan(
            [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)],
            resourceHealthIndexInvalidated: false);

        Assert.IsTrue(plan.RequiresInstalledLookupFullInvalidate);
        Assert.IsFalse(plan.ResourceHealthIndexInvalidated);
        Assert.IsFalse(plan.WarningPresentationChanged);
        Assert.IsTrue(plan.BmsFilesStorageRowsChanged);
        Assert.IsFalse(plan.BmsonSongsStorageRowsChanged);
    }

    private static TestableBmsFile CreateBmsFile(string path, string hash)
    {
        var file = new TestableBmsFile();
        file.path = path;
        file.SetHash(hash);
        return file;
    }

    private sealed class CapturingHost : IOwnedChartDigestMutationDispatchHost
    {
        public OwnedChartDigestMutationPlan LastPlan { get; private set; } = null!;

        public string LastReason { get; private set; } = null!;

        public void DispatchOwnedChartDigestMutation(OwnedChartDigestMutationPlan plan, string reason)
        {
            LastPlan = plan;
            LastReason = reason;
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
