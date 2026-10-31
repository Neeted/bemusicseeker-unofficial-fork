using System;
using System.Globalization;
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

    /// <summary>ar-SAで更新成功は固定Gregorian日時と型付き検査時刻を保ち、成功を警告扱いにする誤りを検出します。</summary>
    [TestMethod]
    public void Create_SuccessUpdated_ReturnsUpdatedStatus()
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
            var result = PlaylistSyncAttemptResult.CreateSuccess(null, null, new Uri("https://example.com/table.html"), updated: true);

            PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_sync_status_updated, status.StatusText);
            Assert.IsFalse(status.HasFailureStatus);
            Assert.AreEqual(new DateTime(2026, 3, 8, 20, 0, 0), status.CheckedAt);
            StringAssert.Contains(status.Detail, "2026-03-08 20:00:00");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    /// <summary>th-THで見出し解析失敗は固定Gregorian日時と対象URL・元状態を保ち、原因のない正常表示へ丸める誤りを検出します。</summary>
    [TestMethod]
    public void Create_FailureHeaderParse_ReturnsWarningStatus()
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
            var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new ArgumentException("header", "_header_json"));

            PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, new DateTime(2026, 3, 8, 20, 0, 0));

            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_sync_status_header, status.StatusText);
            Assert.IsTrue(status.HasFailureStatus);
            StringAssert.Contains(status.Detail, "https://example.com/header.json");
            StringAssert.Contains(status.Detail, "2026-03-08 20:00:00");
            Assert.AreEqual(new DateTime(2026, 3, 8, 20, 0, 0), status.CheckedAt);
            Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, status.Kind);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }
}
