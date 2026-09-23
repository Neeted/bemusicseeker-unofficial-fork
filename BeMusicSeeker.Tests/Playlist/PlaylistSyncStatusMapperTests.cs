using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSyncStatusMapperTests
{
    [TestInitialize]
    public void Initialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ResourceService.Current.ChangeCulture("ja-JP");
    }

    [TestMethod]
    public void CreateNone_ReturnsNonFailureDash()
    {
        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.CreateNone();

        Assert.AreEqual("-", status.StatusText);
        Assert.IsFalse(status.HasFailureStatus);
        Assert.AreEqual(120, status.StatusSortOrder);
    }

    [TestMethod]
    public void Create_SuccessUpdated_ReturnsUpdatedStatus()
    {
        var result = PlaylistSyncAttemptResult.CreateSuccess(null, null, new Uri("https://example.com/table.html"), updated: true);

        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

        Assert.AreEqual("UPDATED", status.StatusText);
        Assert.IsFalse(status.HasFailureStatus);
        Assert.AreEqual(100, status.StatusSortOrder);
        StringAssert.Contains(status.Detail, "2026/03/08 20:00:00");
    }

    [TestMethod]
    public void Create_FailureHeaderParse_ReturnsWarningStatus()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new ArgumentException("header", "_header_json"));

        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

        Assert.AreEqual("HEADER", status.StatusText);
        Assert.IsTrue(status.HasFailureStatus);
        Assert.AreEqual(20, status.StatusSortOrder);
        StringAssert.Contains(status.Detail, "https://example.com/header.json");
    }
}
