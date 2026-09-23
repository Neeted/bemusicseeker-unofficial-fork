using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ribbit.Net;

namespace BeMusicSeeker.Models;

/// <summary>
/// プレイリスト行の MD5 から本体パッケージの候補 URL を検索する外部 API を表します。
/// 外部 API ごとの差異を閉じ込め、UI 側が優先順や JSON 形状を意識しないようにします。
/// </summary>
internal interface IPlaylistExternalPackageLookupProvider
{
    /// <summary>
    /// ログやテスト結果で provider を識別するための短い名前です。
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// 指定した譜面 MD5 から本体パッケージの直接ダウンロード候補を検索します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <param name="cancellationToken">検索を中断するためのトークン。</param>
    /// <returns>候補が見つかった場合は検索結果。候補がない、またはレスポンス形状が不正な場合は <see langword="null"/>。</returns>
    Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken);
}

/// <summary>
/// 外部 API 検索用の HTTP 取得境界です。
/// テストではライブ API に依存せず JSON 解析と優先順だけを確認できるようにします。
/// </summary>
internal interface IPlaylistExternalPackageLookupHttpClient
{
    /// <summary>
    /// 指定 URI のレスポンス本文を文字列として取得します。
    /// </summary>
    /// <param name="uri">取得先 URI。</param>
    /// <param name="cancellationToken">取得を中断するためのトークン。</param>
    /// <returns>レスポンス本文。</returns>
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="AppHttpClient"/> を外部パッケージ検索用 HTTP 境界として使うアダプタです。
/// </summary>
internal sealed class AppPlaylistExternalPackageLookupHttpClient : IPlaylistExternalPackageLookupHttpClient
{
    private readonly AppHttpClient httpClient;

    /// <summary>
    /// 共有 HTTP クライアントを使って初期化します。
    /// </summary>
    internal AppPlaylistExternalPackageLookupHttpClient()
        : this(AppHttpClient.Shared)
    {
    }

    /// <summary>
    /// 指定した HTTP クライアントを使って初期化します。
    /// </summary>
    /// <param name="httpClient">レスポンス本文の取得に使う HTTP クライアント。</param>
    internal AppPlaylistExternalPackageLookupHttpClient(AppHttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        return httpClient.GetStringAsync(uri, Encoding.UTF8, cancellationToken);
    }
}

/// <summary>
/// 外部 API から取得した本体パッケージ候補を表します。
/// </summary>
internal sealed class PlaylistExternalPackageLookupResult
{
    /// <summary>
    /// 検索結果を作成します。
    /// </summary>
    /// <param name="providerId">候補を返した provider の識別名。</param>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <param name="downloadUri">直接ダウンロード相当として扱う候補 URL。</param>
    /// <param name="fileName">API が返したファイル名。未提供の場合は空文字列。</param>
    /// <param name="fileSize">API が返したファイルサイズ。未提供の場合は <see langword="null"/>。</param>
    internal PlaylistExternalPackageLookupResult(string providerId, string chartMd5, Uri downloadUri, string fileName = null, long? fileSize = null)
    {
        ProviderId = providerId ?? string.Empty;
        ChartMd5 = chartMd5 ?? string.Empty;
        DownloadUri = downloadUri ?? throw new ArgumentNullException(nameof(downloadUri));
        FileName = fileName ?? string.Empty;
        FileSize = fileSize;
    }

    /// <summary>
    /// 候補を返した provider の識別名です。
    /// </summary>
    internal string ProviderId { get; }

    /// <summary>
    /// 検索対象の譜面 MD5 です。
    /// </summary>
    internal string ChartMd5 { get; }

    /// <summary>
    /// 直接ダウンロード相当として扱う候補 URL です。
    /// </summary>
    internal Uri DownloadUri { get; }

    /// <summary>
    /// API が返したファイル名です。ダウンロード時の最終ファイル名は HTTP 応答から再解決します。
    /// </summary>
    internal string FileName { get; }

    /// <summary>
    /// API が返したファイルサイズです。信頼境界外の値なので、ダウンロード時のサイズ検査は別途行います。
    /// </summary>
    internal long? FileSize { get; }
}

/// <summary>
/// 外部 API から得た候補 URL のダウンロード試行結果です。
/// </summary>
internal enum PlaylistExternalPackageDownloadAttemptKind
{
    /// <summary>
    /// 対応ファイルとして保存できました。
    /// </summary>
    Downloaded,

    /// <summary>
    /// すでに成功済みの URL と同一でした。
    /// </summary>
    DuplicateDownloadedUrl,

    /// <summary>
    /// サイズ上限により保存しませんでした。
    /// </summary>
    BlockedBySizeLimit,

    /// <summary>
    /// 直接ダウンロード相当の対応ファイルではありませんでした。
    /// </summary>
    Unsupported,

    /// <summary>
    /// 通信エラーなどにより保存できませんでした。
    /// </summary>
    Failed
}

/// <summary>
/// 外部 API から得た候補 URL のダウンロード試行結果を保持します。
/// </summary>
internal sealed class PlaylistExternalPackageDownloadAttempt
{
    private PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind kind, string filePath = null, string downloadKey = null)
    {
        Kind = kind;
        FilePath = filePath ?? string.Empty;
        DownloadKey = downloadKey ?? string.Empty;
    }

    /// <summary>
    /// 試行結果の種類です。
    /// </summary>
    internal PlaylistExternalPackageDownloadAttemptKind Kind { get; }

    /// <summary>
    /// 保存できたファイルパスです。
    /// </summary>
    internal string FilePath { get; }

    /// <summary>
    /// ダウンロード URL の重複判定キーです。
    /// </summary>
    internal string DownloadKey { get; }

    /// <summary>
    /// 保存成功の結果を作成します。
    /// </summary>
    /// <param name="filePath">保存先ファイルパス。</param>
    /// <param name="downloadKey">ダウンロード URL の重複判定キー。</param>
    /// <returns>保存成功の結果。</returns>
    internal static PlaylistExternalPackageDownloadAttempt Downloaded(string filePath, string downloadKey)
    {
        return new PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind.Downloaded, filePath, downloadKey);
    }

    /// <summary>
    /// 成功済み URL 重複の結果を作成します。
    /// </summary>
    /// <param name="downloadKey">重複したダウンロード URL の判定キー。</param>
    /// <returns>成功済み URL 重複の結果。</returns>
    internal static PlaylistExternalPackageDownloadAttempt DuplicateDownloadedUrl(string downloadKey)
    {
        return new PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind.DuplicateDownloadedUrl, downloadKey: downloadKey);
    }

    /// <summary>
    /// サイズ上限による中断結果を作成します。
    /// </summary>
    /// <returns>サイズ上限による中断結果。</returns>
    internal static PlaylistExternalPackageDownloadAttempt BlockedBySizeLimit(string downloadKey = null)
    {
        return new PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind.BlockedBySizeLimit, downloadKey: downloadKey);
    }

    /// <summary>
    /// 非対応 URL の結果を作成します。
    /// </summary>
    /// <returns>非対応 URL の結果。</returns>
    internal static PlaylistExternalPackageDownloadAttempt Unsupported(string downloadKey = null)
    {
        return new PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind.Unsupported, downloadKey: downloadKey);
    }

    /// <summary>
    /// 失敗結果を作成します。
    /// </summary>
    /// <returns>失敗結果。</returns>
    internal static PlaylistExternalPackageDownloadAttempt Failed(string downloadKey = null)
    {
        return new PlaylistExternalPackageDownloadAttempt(PlaylistExternalPackageDownloadAttemptKind.Failed, downloadKey: downloadKey);
    }
}

/// <summary>
/// 外部 API 検索とダウンロード試行を優先順に進めた結果の種類です。
/// </summary>
internal enum PlaylistExternalPackageWorkflowResultKind
{
    /// <summary>
    /// 対応ファイルとして保存できました。
    /// </summary>
    Downloaded,

    /// <summary>
    /// どの provider からも候補 URL を取得できませんでした。
    /// </summary>
    NoCandidate,

    /// <summary>
    /// すでに成功済みの URL が返されたため処理しませんでした。
    /// </summary>
    DuplicateDownloadedUrl,

    /// <summary>
    /// 同一実行中に失敗済みの URL しか得られなかったため再試行しませんでした。
    /// </summary>
    DuplicateFailedUrl,

    /// <summary>
    /// すべての候補がサイズ上限で保存できませんでした。
    /// </summary>
    BlockedBySizeLimit,

    /// <summary>
    /// すべての候補が対応ファイルではありませんでした。
    /// </summary>
    Unsupported,

    /// <summary>
    /// provider またはダウンロードで失敗しました。
    /// </summary>
    Failed
}

/// <summary>
/// 外部 API 検索とダウンロード試行を優先順に進めた結果を表します。
/// </summary>
internal sealed class PlaylistExternalPackageWorkflowResult
{
    private PlaylistExternalPackageWorkflowResult(PlaylistExternalPackageWorkflowResultKind kind, PlaylistExternalPackageLookupResult lookupResult = null, string filePath = null, string downloadKey = null)
    {
        Kind = kind;
        LookupResult = lookupResult;
        FilePath = filePath ?? string.Empty;
        DownloadKey = downloadKey ?? string.Empty;
    }

    /// <summary>
    /// ワークフロー結果の種類です。
    /// </summary>
    internal PlaylistExternalPackageWorkflowResultKind Kind { get; }

    /// <summary>
    /// 最終的に扱った候補です。
    /// </summary>
    internal PlaylistExternalPackageLookupResult LookupResult { get; }

    /// <summary>
    /// 保存できたファイルパスです。
    /// </summary>
    internal string FilePath { get; }

    /// <summary>
    /// ダウンロード URL の重複判定キーです。
    /// </summary>
    internal string DownloadKey { get; }

    /// <summary>
    /// 保存成功の結果を作成します。
    /// </summary>
    /// <param name="lookupResult">保存できた候補。</param>
    /// <param name="filePath">保存先ファイルパス。</param>
    /// <param name="downloadKey">ダウンロード URL の重複判定キー。</param>
    /// <returns>保存成功の結果。</returns>
    internal static PlaylistExternalPackageWorkflowResult Downloaded(PlaylistExternalPackageLookupResult lookupResult, string filePath, string downloadKey)
    {
        return new PlaylistExternalPackageWorkflowResult(PlaylistExternalPackageWorkflowResultKind.Downloaded, lookupResult, filePath, downloadKey);
    }

    /// <summary>
    /// 指定種類の非保存結果を作成します。
    /// </summary>
    /// <param name="kind">非保存結果の種類。</param>
    /// <param name="lookupResult">最後に扱った候補。</param>
    /// <param name="downloadKey">ダウンロード URL の重複判定キー。</param>
    /// <returns>非保存結果。</returns>
    internal static PlaylistExternalPackageWorkflowResult Skipped(PlaylistExternalPackageWorkflowResultKind kind, PlaylistExternalPackageLookupResult lookupResult = null, string downloadKey = null)
    {
        return new PlaylistExternalPackageWorkflowResult(kind, lookupResult, downloadKey: downloadKey);
    }
}

/// <summary>
/// Ginger のメタ API から本体パッケージ候補を取得します。
/// </summary>
internal sealed class GingerPlaylistExternalPackageLookupProvider : IPlaylistExternalPackageLookupProvider
{
    /// <summary>
    /// Ginger provider の識別名です。
    /// </summary>
    internal const string ProviderIdValue = "Ginger";

    private readonly IPlaylistExternalPackageLookupHttpClient httpClient;

    /// <summary>
    /// 共有 HTTP クライアントを使って初期化します。
    /// </summary>
    internal GingerPlaylistExternalPackageLookupProvider()
        : this(new AppPlaylistExternalPackageLookupHttpClient())
    {
    }

    /// <summary>
    /// 指定した HTTP クライアントを使って初期化します。
    /// </summary>
    /// <param name="httpClient">API レスポンスの取得に使う HTTP クライアント。</param>
    internal GingerPlaylistExternalPackageLookupProvider(IPlaylistExternalPackageLookupHttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public string ProviderId => ProviderIdValue;

    /// <inheritdoc />
    public async Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
    {
        Uri requestUri = BuildRequestUri(chartMd5);
        string json = await httpClient.GetStringAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return TryParseLookupResult(chartMd5, json, out PlaylistExternalPackageLookupResult result) ? result : null;
    }

    /// <summary>
    /// Ginger API のリクエスト URI を作成します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <returns>Ginger API のリクエスト URI。</returns>
    internal static Uri BuildRequestUri(string chartMd5)
    {
        return new Uri("https://gingerrush.com/download/package/" + Uri.EscapeDataString((chartMd5 ?? string.Empty).ToLowerInvariant()));
    }

    /// <summary>
    /// Ginger API の JSON から候補 URL を取り出します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <param name="json">API レスポンス本文。</param>
    /// <param name="result">候補が見つかった場合の検索結果。</param>
    /// <returns>候補を取得できた場合は <see langword="true"/>。</returns>
    internal static bool TryParseLookupResult(string chartMd5, string json, out PlaylistExternalPackageLookupResult result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            var root = JObject.Parse(json);
            // NOTE:
            // Ginger は downloadURL を直ダウンロード相当の契約点として公開しているため、
            // md5s や shardMD5 は補助情報として扱い、導入可否は既存ダウンロード検証で判断します。
            string downloadUrl = root.Value<string>("downloadURL");
            if (!PlaylistExternalPackageLookupUriSupport.TryCreateDirectDownloadCandidateUri(downloadUrl, out Uri downloadUri))
            {
                return false;
            }
            result = new PlaylistExternalPackageLookupResult(
                ProviderIdValue,
                NormalizeMd5(chartMd5),
                downloadUri,
                root.Value<string>("fileName"),
                root.Value<long?>("fileSize"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeMd5(string chartMd5)
    {
        return (chartMd5 ?? string.Empty).ToLowerInvariant();
    }
}

/// <summary>
/// Konmai のメタ API から本体パッケージ候補を取得します。
/// </summary>
internal sealed class KonmaiPlaylistExternalPackageLookupProvider : IPlaylistExternalPackageLookupProvider
{
    /// <summary>
    /// Konmai provider の識別名です。
    /// </summary>
    internal const string ProviderIdValue = "Konmai";

    private readonly IPlaylistExternalPackageLookupHttpClient httpClient;

    /// <summary>
    /// 共有 HTTP クライアントを使って初期化します。
    /// </summary>
    internal KonmaiPlaylistExternalPackageLookupProvider()
        : this(new AppPlaylistExternalPackageLookupHttpClient())
    {
    }

    /// <summary>
    /// 指定した HTTP クライアントを使って初期化します。
    /// </summary>
    /// <param name="httpClient">API レスポンスの取得に使う HTTP クライアント。</param>
    internal KonmaiPlaylistExternalPackageLookupProvider(IPlaylistExternalPackageLookupHttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public string ProviderId => ProviderIdValue;

    /// <inheritdoc />
    public async Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
    {
        Uri requestUri = BuildRequestUri(chartMd5);
        string json = await httpClient.GetStringAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return TryParseLookupResult(chartMd5, json, out PlaylistExternalPackageLookupResult result) ? result : null;
    }

    /// <summary>
    /// Konmai API のリクエスト URI を作成します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <returns>Konmai API のリクエスト URI。</returns>
    internal static Uri BuildRequestUri(string chartMd5)
    {
        return new Uri("https://bms.alvorna.com/api/hash?md5=" + Uri.EscapeDataString((chartMd5 ?? string.Empty).ToLowerInvariant()));
    }

    /// <summary>
    /// Konmai API の JSON から候補 URL を取り出します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <param name="json">API レスポンス本文。</param>
    /// <param name="result">候補が見つかった場合の検索結果。</param>
    /// <returns>候補を取得できた場合は <see langword="true"/>。</returns>
    internal static bool TryParseLookupResult(string chartMd5, string json, out PlaylistExternalPackageLookupResult result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            var root = JObject.Parse(json);
            // NOTE:
            // Konmai は成功時に data.song_url を返す契約だが、エラー形状にも data が混在する可能性を避けるため
            // result == success も確認してから URL を採用します。
            if (!string.Equals(root.Value<string>("result"), "success", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string downloadUrl = root["data"]?.Value<string>("song_url");
            if (!PlaylistExternalPackageLookupUriSupport.TryCreateDirectDownloadCandidateUri(downloadUrl, out Uri downloadUri))
            {
                return false;
            }
            result = new PlaylistExternalPackageLookupResult(
                ProviderIdValue,
                NormalizeMd5(chartMd5),
                downloadUri,
                root["data"]?.Value<string>("song_name"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeMd5(string chartMd5)
    {
        return (chartMd5 ?? string.Empty).ToLowerInvariant();
    }
}

/// <summary>
/// 複数の外部 API provider を優先順に扱い、候補 URL のダウンロード試行までを制御します。
/// </summary>
internal sealed class PlaylistExternalPackageLookupService
{
    private readonly IReadOnlyList<IPlaylistExternalPackageLookupProvider> providers;

    /// <summary>
    /// 指定した provider 群を優先順として初期化します。
    /// </summary>
    /// <param name="providers">優先順に並べた provider 群。</param>
    internal PlaylistExternalPackageLookupService(IEnumerable<IPlaylistExternalPackageLookupProvider> providers)
    {
        this.providers = [.. providers ?? throw new ArgumentNullException(nameof(providers))];
        if (this.providers.Count == 0)
        {
            throw new ArgumentException("At least one lookup provider is required.", nameof(providers));
        }
    }

    /// <summary>
    /// 既定の provider 優先順でサービスを作成します。
    /// </summary>
    /// <returns>Ginger、Konmai の順に問い合わせるサービス。</returns>
    internal static PlaylistExternalPackageLookupService CreateDefault()
    {
        return new PlaylistExternalPackageLookupService(
        [
            new GingerPlaylistExternalPackageLookupProvider(),
            new KonmaiPlaylistExternalPackageLookupProvider()
        ]);
    }

    /// <summary>
    /// 登録済み provider を優先順で返します。
    /// </summary>
    internal IReadOnlyList<IPlaylistExternalPackageLookupProvider> Providers => providers;

    /// <summary>
    /// provider を優先順に問い合わせ、最初に導入可能だった候補を返します。
    /// </summary>
    /// <param name="chartMd5">検索対象の譜面 MD5。</param>
    /// <param name="downloadAsync">候補 URL のダウンロードを試行する関数。第 2 引数には処理中断用トークンが渡されます。</param>
    /// <param name="downloadedDownloadKeys">成功済み URL の重複判定キー。</param>
    /// <param name="failedDownloadKeys">失敗済み URL の重複判定キー。</param>
    /// <param name="downloadKeyFactory">候補 URL から重複判定キーを作成する関数。</param>
    /// <param name="log">診断ログを出力する関数。</param>
    /// <param name="cancellationToken">処理を中断するためのトークン。</param>
    /// <returns>優先順検索とダウンロード試行の結果。</returns>
    internal async Task<PlaylistExternalPackageWorkflowResult> DownloadFirstAvailablePackageAsync(
        string chartMd5,
        Func<PlaylistExternalPackageLookupResult, CancellationToken, Task<PlaylistExternalPackageDownloadAttempt>> downloadAsync,
        ISet<string> downloadedDownloadKeys,
        ISet<string> failedDownloadKeys,
        Func<Uri, string> downloadKeyFactory,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (downloadAsync == null)
        {
            throw new ArgumentNullException(nameof(downloadAsync));
        }

        bool sawLookupFailure = false;
        bool sawFailedDuplicate = false;
        PlaylistExternalPackageWorkflowResult lastDownloadFailure = null;
        foreach (IPlaylistExternalPackageLookupProvider provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaylistExternalPackageLookupResult lookupResult;
            try
            {
                lookupResult = await provider.LookupAsync(chartMd5, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sawLookupFailure = true;
                log?.Invoke("playlist_external_package_lookup provider_failed provider=" + provider.ProviderId + " md5=" + chartMd5 + " errorType=" + ex.GetType().FullName + " error=" + SanitizeLogValue(ex.Message));
                continue;
            }

            if (lookupResult == null)
            {
                log?.Invoke("playlist_external_package_lookup no_candidate provider=" + provider.ProviderId + " md5=" + chartMd5);
                continue;
            }

            string downloadKey = CreateDownloadKey(downloadKeyFactory, lookupResult.DownloadUri);
            if (!string.IsNullOrWhiteSpace(downloadKey) && downloadedDownloadKeys?.Contains(downloadKey) == true)
            {
                log?.Invoke("playlist_external_package_lookup duplicate_downloaded provider=" + provider.ProviderId + " md5=" + chartMd5 + " url=" + lookupResult.DownloadUri + " key=" + downloadKey);
                return PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl, lookupResult, downloadKey);
            }
            if (!string.IsNullOrWhiteSpace(downloadKey) && failedDownloadKeys?.Contains(downloadKey) == true)
            {
                sawFailedDuplicate = true;
                log?.Invoke("playlist_external_package_lookup duplicate_failed provider=" + provider.ProviderId + " md5=" + chartMd5 + " url=" + lookupResult.DownloadUri + " key=" + downloadKey);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            PlaylistExternalPackageDownloadAttempt attempt;
            try
            {
                attempt = await downloadAsync(lookupResult, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddFailedDownloadKey(failedDownloadKeys, downloadKey);
                log?.Invoke("playlist_external_package_lookup download_failed provider=" + provider.ProviderId + " md5=" + chartMd5 + " url=" + lookupResult.DownloadUri + " errorType=" + ex.GetType().FullName + " error=" + SanitizeLogValue(ex.Message));
                lastDownloadFailure = PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.Failed, lookupResult, downloadKey);
                continue;
            }
            switch (attempt?.Kind)
            {
                case PlaylistExternalPackageDownloadAttemptKind.Downloaded:
                    string successfulDownloadKey = FirstNonEmpty(attempt.DownloadKey, downloadKey);
                    if (!string.IsNullOrWhiteSpace(successfulDownloadKey))
                    {
                        downloadedDownloadKeys?.Add(successfulDownloadKey);
                    }
                    return PlaylistExternalPackageWorkflowResult.Downloaded(lookupResult, attempt.FilePath, successfulDownloadKey);
                case PlaylistExternalPackageDownloadAttemptKind.DuplicateDownloadedUrl:
                    return PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl, lookupResult, FirstNonEmpty(attempt.DownloadKey, downloadKey));
                case PlaylistExternalPackageDownloadAttemptKind.BlockedBySizeLimit:
                    string blockedDownloadKey = FirstNonEmpty(attempt.DownloadKey, downloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, blockedDownloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, downloadKey);
                    lastDownloadFailure = PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.BlockedBySizeLimit, lookupResult, blockedDownloadKey);
                    break;
                case PlaylistExternalPackageDownloadAttemptKind.Unsupported:
                    string unsupportedDownloadKey = FirstNonEmpty(attempt.DownloadKey, downloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, unsupportedDownloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, downloadKey);
                    lastDownloadFailure = PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.Unsupported, lookupResult, unsupportedDownloadKey);
                    break;
                default:
                    string failedDownloadKey = FirstNonEmpty(attempt?.DownloadKey, downloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, failedDownloadKey);
                    AddFailedDownloadKey(failedDownloadKeys, downloadKey);
                    lastDownloadFailure = PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.Failed, lookupResult, failedDownloadKey);
                    break;
            }
        }

        if (lastDownloadFailure != null)
        {
            return lastDownloadFailure;
        }
        if (sawFailedDuplicate)
        {
            return PlaylistExternalPackageWorkflowResult.Skipped(PlaylistExternalPackageWorkflowResultKind.DuplicateFailedUrl);
        }
        return PlaylistExternalPackageWorkflowResult.Skipped(sawLookupFailure ? PlaylistExternalPackageWorkflowResultKind.Failed : PlaylistExternalPackageWorkflowResultKind.NoCandidate);
    }

    private static void AddFailedDownloadKey(ISet<string> failedDownloadKeys, string downloadKey)
    {
        if (!string.IsNullOrWhiteSpace(downloadKey))
        {
            failedDownloadKeys?.Add(downloadKey);
        }
    }

    private static string CreateDownloadKey(Func<Uri, string> downloadKeyFactory, Uri downloadUri)
    {
        if (downloadUri == null)
        {
            return string.Empty;
        }
        return downloadKeyFactory?.Invoke(downloadUri) ?? downloadUri.AbsoluteUri;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string SanitizeLogValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }
}

/// <summary>
/// 外部 API から得た URL をダウンロード候補として扱える形か検証します。
/// </summary>
internal static class PlaylistExternalPackageLookupUriSupport
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".lzh"];

    /// <summary>
    /// HTTP/HTTPS の absolute URI かつ直ダウンロード相当のファイル名に見える URL のみを候補として受け入れます。
    /// </summary>
    /// <param name="value">検証する URL 文字列。</param>
    /// <param name="uri">検証に成功した URI。</param>
    /// <returns>直ダウンロード相当の候補 URL の場合は <see langword="true"/>。</returns>
    internal static bool TryCreateDirectDownloadCandidateUri(string value, out Uri uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri parsedUri))
        {
            return false;
        }
        if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!IsDirectDownloadCandidateFileName(Path.GetFileName(parsedUri.AbsolutePath)))
        {
            return false;
        }
        uri = parsedUri;
        return true;
    }

    private static bool IsDirectDownloadCandidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        if (ChartFileKindResolver.IsSupportedChartFilePath(fileName))
        {
            return true;
        }
        return ArchiveExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}
