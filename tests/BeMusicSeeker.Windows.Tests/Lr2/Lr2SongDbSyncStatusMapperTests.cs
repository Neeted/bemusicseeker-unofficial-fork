using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusMapperTests
{
    private static string Catalog(string culture, string key)
    {
        Assert.IsTrue(JsonLanguageCatalog.TryGetString(culture, key, out string value), key);
        return value;
    }

    /// <summary>
    /// 一度だけ作った同じ状態インスタンスが、日本語→英語→日本語の実辞書切替えで現在語の状態名・進捗・詳細を返し、
    /// 種別・段階件数・進捗値・警告判定・検査日時・要求が変わらないことを確認します。
    /// 保存位置の数値、既知の段階で始まる診断の先頭だけの置換、未知の段階や他形式の先頭の原文保持も含み、固定言語の再利用や翻訳経路の未接続を検出します。
    /// </summary>
    /// <remarks>共有表示文化圏を変更して復元するため、このメソッドだけ並列実行を止めます。入口は本番の <see cref="ResourceService.ChangeCulture"/> です。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [DataRow("running")]
    [DataRow("needed")]
    [DataRow("failedKnownPrefix")]
    [DataRow("failedUnknownStage")]
    [DataRow("failedOtherPrefix")]
    [DataRow("incomplete")]
    [DataRow("cancelled")]
    public void RuntimeStatus_SameInstanceFollowsLanguageAndKeepsFacts(string scenario)
    {
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            DateTime checkedAt = new(2026, 3, 8, 20, 0, 0);
            var request = new OperationProgressRequest(1, 2, "lr2_song_db_sync", 3);
            Lr2SongDbSyncStatusSnapshot snapshot = scenario switch
            {
                "running" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Running, Stage = "song_rows", StageProcessedCount = 2, StageTotalCount = 5, ProcessedCursor = 10, TotalCount = 100 },
                "needed" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Needed },
                "failedKnownPrefix" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Failed, Stage = "song_rows", LastError = "song_rows: 原文 raw", ProcessedCursor = 3, TotalCount = 9 },
                "failedUnknownStage" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Failed, Stage = "unknown_stage_x", LastError = "unknown_stage_x: 原文 raw" },
                "failedOtherPrefix" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Failed, Stage = "song_rows", LastError = "other_stage: 原文 raw" },
                "incomplete" => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Incomplete, Stage = "folder_saving" },
                _ => new Lr2SongDbSyncStatusSnapshot { Status = Lr2SongDbSyncStatusKind.Cancelled, Stage = "cancelled" }
            };
            Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(snapshot, checkedAt, request);
            (Lr2SongDbSyncStatusKind Kind, double Value, double Maximum, bool HasProgress, bool Warning, DateTime At, OperationProgressRequest Request) Facts() =>
                (status.Kind, status.ProgressValue, status.ProgressMaximum, status.HasProgress, status.HasWarningStatus, status.CheckedAt, status.Request);
            (Lr2SongDbSyncStatusKind Kind, double Value, double Maximum, bool HasProgress, bool Warning, DateTime At, OperationProgressRequest Request) facts = Facts();

            string StatusKey() => status.Kind switch
            {
                Lr2SongDbSyncStatusKind.Needed => nameof(Resources.Lr2_song_db_sync_status_needed),
                Lr2SongDbSyncStatusKind.Running => nameof(Resources.Lr2_song_db_sync_status_running),
                Lr2SongDbSyncStatusKind.Failed => nameof(Resources.Lr2_song_db_sync_status_failed),
                _ => nameof(Resources.Lr2_song_db_sync_status_incomplete)
            };
            string StageText(string culture) => snapshot.Stage switch
            {
                null => string.Empty,
                "song_rows" => Catalog(culture, nameof(Resources.Lr2_song_db_sync_stage_song_rows)),
                "folder_saving" => Catalog(culture, nameof(Resources.Lr2_song_db_sync_stage_folder_saving)),
                "cancelled" => Catalog(culture, nameof(Resources.Lr2_song_db_sync_status_incomplete)),
                var other => other
            };
            void Assert_(string culture)
            {
                Assert.AreEqual(Catalog(culture, StatusKey()), status.StatusText, culture);
                string stage = StageText(culture);
                string progress = snapshot.StageTotalCount > 0 ? stage + " [" + snapshot.StageProcessedCount + "/" + snapshot.StageTotalCount + "]" : stage;
                Assert.AreEqual(progress, status.ProgressText, culture);
                var lines = new List<string> { DateTimeDisplayFormatter.FormatTimestamp(checkedAt) };
                if (snapshot.TotalCount > 0) { lines.Add(string.Format(Catalog(culture, nameof(Resources.Lr2_song_db_sync_saved_position)), snapshot.ProcessedCursor, snapshot.TotalCount)); }
                if (!string.IsNullOrWhiteSpace(stage)) { lines.Add(stage); }
                string? error = snapshot.LastError;
                if (error != null && snapshot.Stage == "song_rows" && error.StartsWith("song_rows: ", StringComparison.Ordinal)) { error = stage + ": " + error["song_rows: ".Length..]; }
                if (!string.IsNullOrWhiteSpace(error)) { lines.Add(error); }
                Assert.AreEqual(string.Join(Environment.NewLine, lines), status.Detail, culture);
                Assert.AreEqual(facts, Facts(), culture);
            }

            Assert_("ja-JP");
            string japaneseStatus = status.StatusText;
            string japaneseDetail = status.Detail;
            ResourceService.Current.ChangeCulture("en-US");
            Assert_("en-US");
            Assert.AreNotEqual(japaneseStatus, status.StatusText);
            if (scenario is "running" or "failedKnownPrefix" or "incomplete") { Assert.AreNotEqual(japaneseDetail, status.Detail); }
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert_("ja-JP");
            Assert.AreEqual(japaneseStatus, status.StatusText);
            Assert.AreEqual(japaneseDetail, status.Detail);
            if (scenario == "failedUnknownStage") { StringAssert.Contains(status.Detail, "unknown_stage_x: 原文 raw"); }
            if (scenario == "failedOtherPrefix") { StringAssert.Contains(status.Detail, "other_stage: 原文 raw"); }
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }
}
