using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSyncStatusMapperTests
{
    /// <summary>未試行状態は未同期用リソースを選び失敗表示を出さず、未実施を警告扱いにする誤りを検出します。</summary>
    [TestMethod]
    public void CreateNone_ReturnsNonFailureDash()
    {
        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.CreateNone();

        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_sync_status_none, status.StatusText);
        Assert.IsFalse(status.HasFailureStatus);
    }

    /// <summary>更新成功は更新用リソースと検査時刻を詳細へ渡し失敗表示を出さず、成功を警告扱いにする誤りを検出します。</summary>
    [TestMethod]
    public void Create_SuccessUpdated_ReturnsUpdatedStatus()
    {
        var result = PlaylistSyncAttemptResult.CreateSuccess(null, null, new Uri("https://example.com/table.html"), updated: true);

        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_sync_status_updated, status.StatusText);
        Assert.IsFalse(status.HasFailureStatus);
        StringAssert.Contains(status.Detail, "2026/03/08 20:00:00");
    }

    /// <summary>見出し解析失敗は見出し用リソースと対象URLを示して失敗表示を有効にし、原因のない正常表示へ丸める誤りを検出します。</summary>
    [TestMethod]
    public void Create_FailureHeaderParse_ReturnsWarningStatus()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new ArgumentException("header", "_header_json"));

        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_sync_status_header, status.StatusText);
        Assert.IsTrue(status.HasFailureStatus);
        StringAssert.Contains(status.Detail, "https://example.com/header.json");
    }
}
