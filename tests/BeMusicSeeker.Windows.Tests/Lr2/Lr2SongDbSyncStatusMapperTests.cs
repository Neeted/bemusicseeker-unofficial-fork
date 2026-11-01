using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusMapperTests
{

    /// <summary>
    /// 日本語と英語の実辞書へ切替えると同じ工程と進捗が選択言語で表示され、固定言語の再利用や翻訳経路の未接続を検出します。
    /// </summary>
    /// <remarks>共有表示文化圏を変更して復元するため、このメソッドだけ並列実行を止めます。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public void Create_LanguageSwitchUsesActualJapaneseAndEnglishStageResources()
    {
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            string? japanese = null;
            foreach (string culture in new[] { "ja-JP", "en-US" })
            {
                Resources.Culture = CultureInfo.GetCultureInfo(culture);
                Assert.IsTrue(JsonLanguageCatalog.TryGetString(culture, nameof(Resources.Lr2_song_db_sync_stage_song_rows), out string expected));
                Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
                {
                    Status = Lr2SongDbSyncStatusKind.Running,
                    Stage = "song_rows",
                    StageProcessedCount = 2,
                    StageTotalCount = 5
                }, DateTime.MinValue);
                Assert.AreEqual(expected + " [2/5]", status.ProgressText);
                Assert.AreEqual(expected, status.Detail);
                if (japanese == null)
                {
                    japanese = expected;
                }
                else
                {
                    Assert.AreNotEqual(japanese, expected);
                }
            }
        }
        finally
        {
            Resources.Culture = previousCulture;
        }
    }
}
