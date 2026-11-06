namespace BeMusicSeeker.TestSupport.Components;

/// <summary>公開サービスへ接続せず、HTTP取得を即座に失敗させる外部表一覧の宛先です。</summary>
/// <remarks>
/// 閉じたループバックの通常ポート（例: <c>127.0.0.1:1</c>）は、WindowsがRSTを受けた接続を再試行するため、
/// 拒否の確定まで約2秒かかります。ポート0への接続は送信前に <c>WSAEADDRNOTAVAIL</c> で失敗し、
/// 同じ <see cref="System.Net.Http.HttpRequestException"/> の失敗経路を数ミリ秒で通ります。
/// 起動後の外部表取得を実行させる画面fixtureが、取得失敗の時間を1件ごとに負担しないために使います。
/// </remarks>
internal static class UnreachableHttpEndpoint
{
    /// <summary>外部表一覧の取得先として使う、即座に接続失敗するURLです。</summary>
    internal static Uri TableListUrl { get; } = new("http://127.0.0.1:0/table-list.json");
}
