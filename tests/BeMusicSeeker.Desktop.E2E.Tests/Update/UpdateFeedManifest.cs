using System.Text.Json;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>更新確認が受け付ける形式のmanifest(schemaVersion 1、通常版1件)を生成します。更新受入のケース間で再利用します。</summary>
internal static class UpdateFeedManifest
{
    /// <summary>指定した版・配布ページ・資産から、JSON文字列のmanifestを作ります。</summary>
    /// <param name="version">四成分の版です。releaseTagは「v」+版になります。</param>
    /// <param name="releasePageUrl">適用で開かれる配布ページのURLです。</param>
    /// <param name="label">資産の表示名(必須項目)です。</param>
    /// <param name="fileName">資産のファイル名です。</param>
    /// <param name="url">資産の配信URLです。</param>
    /// <param name="sha256">配るbytesから計算した小文字hexです。</param>
    /// <param name="sizeBytes">配るbytesのサイズです。</param>
    internal static string Create(string version, string releasePageUrl, string label, string fileName, string url, string sha256, long sizeBytes)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            releaseTag = "v" + version,
            releasePageUrl,
            packageFormatVersion = 1,
            minimumUpdaterVersion = "1",
            assets = new[]
            {
                new { kind = "app", label, fileName, url, sha256, sizeBytes, includesChartInfoMetadata = false }
            }
        });
}
