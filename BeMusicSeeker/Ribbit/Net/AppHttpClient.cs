using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace Ribbit.Net;

/// <summary>
/// メタデータ付きの HTTP 応答ストリームを表します。
/// 応答メッセージとストリームの寿命をまとめて管理し、呼び出し側は <see cref="IDisposable"/> として扱えます。
/// </summary>
internal sealed class AppHttpResponse : IDisposable
{
    /// <summary>
    /// 元の要求 URI です。
    /// </summary>
    internal Uri RequestUri { get; }

    /// <summary>
    /// リダイレクト後を含む最終的な応答 URI です。
    /// </summary>
    internal Uri ResponseUri { get; }

    /// <summary>
    /// HTTP ステータスコードです。
    /// </summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>
    /// 応答の Content-Length です。未設定時は <see langword="null"/> です。
    /// </summary>
    internal long? ContentLength { get; }

    /// <summary>
    /// HTTP レスポンスヘッダーです。
    /// </summary>
    internal HttpResponseHeaders Headers { get; }

    /// <summary>
    /// コンテンツヘッダーです。
    /// </summary>
    internal HttpContentHeaders ContentHeaders { get; }

    /// <summary>
    /// 応答本文ストリームです。
    /// </summary>
    internal Stream ResponseStream { get; }

    /// <summary>
    /// 背後に保持しているレスポンスメッセージです。
    /// </summary>
    private readonly HttpResponseMessage httpResponseMessage;

    /// <summary>
    /// 破棄済みかどうかを保持します。
    /// </summary>
    private bool disposed;

    /// <summary>
    /// HTTP 応答に基づいてハンドルを初期化します。
    /// </summary>
    /// <param name="requestUri">元の要求 URI。</param>
    /// <param name="httpResponseMessage">保持する応答メッセージ。</param>
    /// <param name="responseStream">保持する応答ストリーム。</param>
    internal AppHttpResponse(Uri requestUri, HttpResponseMessage httpResponseMessage, Stream responseStream)
    {
        if (httpResponseMessage == null)
        {
            throw new ArgumentNullException(nameof(httpResponseMessage));
        }

        RequestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        ResponseUri = httpResponseMessage.RequestMessage?.RequestUri ?? requestUri;
        StatusCode = httpResponseMessage.StatusCode;
        Headers = httpResponseMessage.Headers;
        ContentHeaders = httpResponseMessage.Content?.Headers;
        ContentLength = httpResponseMessage.Content?.Headers?.ContentLength;
        ResponseStream = responseStream ?? throw new ArgumentNullException(nameof(responseStream));
        this.httpResponseMessage = httpResponseMessage;
    }

    /// <summary>
    /// ローカルファイル読み取り用のハンドルを初期化します。
    /// </summary>
    /// <param name="fileUri">対象ファイル URI。</param>
    /// <param name="responseStream">保持するファイルストリーム。</param>
    internal AppHttpResponse(Uri fileUri, Stream responseStream)
    {
        if (responseStream == null)
        {
            throw new ArgumentNullException(nameof(responseStream));
        }
        RequestUri = fileUri ?? throw new ArgumentNullException(nameof(fileUri));
        ResponseUri = fileUri;
        StatusCode = HttpStatusCode.OK;
        Headers = null;
        ContentHeaders = null;
        ContentLength = responseStream.CanSeek ? responseStream.Length : null;
        ResponseStream = responseStream;
    }

    /// <summary>
    /// 保持しているストリームとレスポンスを解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            ResponseStream?.Dispose();
        }
        finally
        {
            httpResponseMessage?.Dispose();
        }
    }
}

/// <summary>
/// アプリ全体で共有する同期ラッパー付き HTTP クライアントです。
/// 内部では <see cref="HttpClient"/> を使い、既存の同期呼び出し経路から段階移行しやすい API を提供します。
/// </summary>
internal sealed class AppHttpClient
{
    /// <summary>
    /// 既定のタイムアウト時間（ミリ秒）です。
    /// </summary>
    internal const int DefaultTimeoutMs = 30000;

    /// <summary>
    /// 既定の User-Agent 文字列です。
    /// </summary>
    private static readonly string DefaultUserAgent = BuildDefaultUserAgent();

    /// <summary>
    /// 標準設定を使う共有インスタンスです。
    /// </summary>
    internal static AppHttpClient Shared { get; } = new AppHttpClient(DefaultTimeoutMs);

    /// <summary>
    /// このインスタンスが内部で使う <see cref="HttpClient"/> です。
    /// 実時間の全体期限は持たせず、期限は要求ごとの取消で <see cref="timeProvider"/> を使って計時します。
    /// </summary>
    private readonly HttpClient httpClient;

    /// <summary>期限を指定しない要求へ使う、クライアント生成時に決めた期限です。要求ごとの期限は共有状態を書き換えません。</summary>
    private readonly TimeSpan defaultTimeout;

    private readonly TimeProvider timeProvider;

    /// <summary>
    /// 本文を自前で制限する呼出し側へ、要求ごとの総期限を渡します。返した期限は呼出し側が破棄します。
    /// </summary>
    /// <param name="timeout">この要求だけの期限。省略時はクライアント生成時の既定期限です。</param>
    internal CancellationTokenSource CreateRequestDeadline(TimeSpan? timeout = null)
        => new(ResolveTimeout(timeout), timeProvider);

    /// <summary>
    /// 指定タイムアウトで HTTP クライアントを初期化します。
    /// </summary>
    /// <param name="requestTimeoutMs">期限を指定しない要求へ使う既定の期限（ミリ秒）。</param>
    private AppHttpClient(int requestTimeoutMs)
        : this(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate },
            requestTimeoutMs,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// 呼出し側が所有する handler と時計で、本番と同じ <see cref="HttpClient"/> の構成と送信・本文読取り経路を作ります。
    /// 並列テストでも共有クライアントや実時間を変更せず、要求ごとの期限を検証できます。
    /// </summary>
    /// <param name="handler">送信を担う handler。破棄は <see cref="HttpClient"/> を通じて行われます。</param>
    /// <param name="requestTimeoutMs">期限を指定しない要求へ使う既定の期限（ミリ秒）。</param>
    /// <param name="timeProvider">期限を計測する時計。</param>
    internal AppHttpClient(HttpMessageHandler handler, int requestTimeoutMs, TimeProvider timeProvider)
        : this(
            CreateHttpClient(handler ?? throw new ArgumentNullException(nameof(handler))),
            TimeSpan.FromMilliseconds(requestTimeoutMs > 0 ? requestTimeoutMs : DefaultTimeoutMs),
            timeProvider)
    {
    }

    /// <summary>
    /// 呼出し側が所有する <see cref="HttpClient"/> と時計で、同じ送信・本文読取り経路を構成します。
    /// <paramref name="httpClient"/> の <see cref="HttpClient.Timeout"/> は期限を指定しない要求の既定期限として引き継ぎ、
    /// 実時間の全体期限を残さないよう無期限へ戻します。
    /// </summary>
    /// <param name="httpClient">未使用のクライアント。破棄は呼出し側が所有します。</param>
    /// <param name="timeProvider">要求ごとの期限を計測する時計。</param>
    internal AppHttpClient(HttpClient httpClient, TimeProvider timeProvider)
        : this(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            AdoptTimeout(httpClient),
            timeProvider)
    {
    }

    private AppHttpClient(HttpClient httpClient, TimeSpan defaultTimeout, TimeProvider timeProvider)
    {
        this.httpClient = httpClient;
        this.defaultTimeout = defaultTimeout;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>本番と test 構成で共有する <see cref="HttpClient"/> の作成です。内部の期限は無期限にし、要求ごとの取消へ任せます。</summary>
    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        return client;
    }

    private static TimeSpan AdoptTimeout(HttpClient client)
    {
        TimeSpan timeout = client.Timeout;
        client.Timeout = Timeout.InfiniteTimeSpan;
        return timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromMilliseconds(DefaultTimeoutMs) : timeout;
    }

    private TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        TimeSpan value = timeout ?? defaultTimeout;
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The request timeout must be positive or infinite.");
        }
        return value;
    }

    /// <summary>
    /// 指定タイムアウトを持つクライアントを生成します。
    /// </summary>
    /// <param name="requestTimeoutMs">期限を指定しない要求へ使う既定の期限（ミリ秒）。</param>
    /// <returns>指定タイムアウトで構成された <see cref="AppHttpClient"/>。</returns>
    internal static AppHttpClient Create(int requestTimeoutMs)
    {
        return new AppHttpClient(requestTimeoutMs);
    }

    /// <summary>
    /// 指定 URI のバイト列を取得します。HTTP は送信開始から本文完了まで一つの期限で読み取ります。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <returns>取得したバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal byte[] GetBytes(Uri uri)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (uri.IsFile)
        {
            return LongPathFileSystem.ReadAllBytes(uri.LocalPath);
        }
        return SendAndReadResponseBytesAsync(HttpMethod.Get, uri).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 指定 URI の内容を文字列として取得します。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="encoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <returns>指定文字コードで解釈した文字列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal string GetString(Uri uri, Encoding encoding = null)
    {
        return DecodeStringAndTrimBom(GetBytes(uri), encoding);
    }

    /// <summary>
    /// 指定 URI の内容を、要求開始から本文完了まで一つの期限で非同期に取得します。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="encoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>指定文字コードで解釈した文字列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal async Task<string> GetStringAsync(Uri uri, Encoding encoding = null, CancellationToken cancellationToken = default)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (uri.IsFile)
        {
            byte[] bytes = await Task.Run(() => LongPathFileSystem.ReadAllBytes(uri.LocalPath), cancellationToken).ConfigureAwait(false);
            return DecodeStringAndTrimBom(bytes, encoding);
        }
        byte[] responseBytes = await SendAndReadResponseBytesAsync(HttpMethod.Get, uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DecodeStringAndTrimBom(responseBytes, encoding);
    }

    /// <summary>
    /// 指定 URI へフォームエンコードされた POST を送信し、本文完了まで一つの期限で読み取ります。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="formData">送信するフォーム値。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="formData"/> が <see langword="null"/> の場合。</exception>
    internal string PostForm(Uri uri, NameValueCollection formData, Encoding responseEncoding = null)
    {
        return PostFormAsync(uri, formData, responseEncoding).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// フォーム POST を、送信開始から本文完了まで一つの期限で非同期に実行します。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="formData">送信するフォーム値。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <param name="cancellationToken">送信と本文読み取りを中断するためのトークン。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="formData"/> が <see langword="null"/> の場合。</exception>
    internal async Task<string> PostFormAsync(Uri uri, NameValueCollection formData, Encoding responseEncoding = null, CancellationToken cancellationToken = default)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (formData == null)
        {
            throw new ArgumentNullException(nameof(formData));
        }
        List<KeyValuePair<string, string>> list = [];
        foreach (string allKey in formData.AllKeys)
        {
            string[] values = formData.GetValues(allKey);
            if (values == null || values.Length == 0)
            {
                list.Add(new KeyValuePair<string, string>(allKey ?? string.Empty, string.Empty));
                continue;
            }
            foreach (string value in values)
            {
                list.Add(new KeyValuePair<string, string>(allKey ?? string.Empty, value ?? string.Empty));
            }
        }
        using var formUrlEncodedContent = new FormUrlEncodedContent(list);
        byte[] responseBytes = await SendAndReadResponseBytesAsync(HttpMethod.Post, uri, formUrlEncodedContent, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DecodeStringAndTrimBom(responseBytes, responseEncoding);
    }

    /// <summary>
    /// 指定 URI へ文字列本文を POST し、本文完了まで一つの期限で読み取ります。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="body">送信する本文。</param>
    /// <param name="contentType">送信時の Content-Type。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <param name="headers">追加するリクエストヘッダー。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="contentType"/> が <see langword="null"/> の場合。</exception>
    internal string PostString(Uri uri, string body, string contentType, Encoding responseEncoding = null, IDictionary<string, string> headers = null)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (contentType == null)
        {
            throw new ArgumentNullException(nameof(contentType));
        }
        Encoding requestEncoding = ResolveContentEncoding(contentType);
        byte[] bytes = requestEncoding.GetBytes(body ?? string.Empty);
        using var byteArrayContent = new ByteArrayContent(bytes);
        byteArrayContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        byte[] responseBytes = SendAndReadResponseBytesAsync(HttpMethod.Post, uri, byteArrayContent, headers).ConfigureAwait(false).GetAwaiter().GetResult();
        return DecodeStringAndTrimBom(responseBytes, responseEncoding);
    }

    /// <summary>
    /// 指定ファイルを multipart/form-data で POST します。ファイル名は MIME 符号化せず UTF-8 で直接送信し、ファイル本体のバイト列を保持します。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="filePath">送信するファイルパス。</param>
    /// <param name="formFieldName">multipart のフィールド名。省略時は <c>file</c>。</param>
    /// <param name="fileName">送信時のファイル名。省略時は元のファイル名。</param>
    /// <param name="additionalFormFields">追加のフォームフィールド。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <param name="headers">追加のリクエストヘッダー。</param>
    /// <param name="logErrorResponseBody">非成功時のレスポンス本文を WARN ログへ含める場合は <see langword="true"/>。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="filePath"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="FileNotFoundException"><paramref name="filePath"/> が存在しない場合。</exception>
    internal string PostFile(Uri uri, string filePath, string formFieldName = "file", string fileName = null, IDictionary<string, string> additionalFormFields = null, Encoding responseEncoding = null, IDictionary<string, string> headers = null, bool logErrorResponseBody = false)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (filePath == null)
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        if (!LongPathFileSystem.FileExists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }
        if (string.IsNullOrWhiteSpace(formFieldName))
        {
            formFieldName = "file";
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(filePath);
        }
        using MultipartFormDataContent multipartFormDataContent = [];
        multipartFormDataContent.HeaderEncodingSelector = (_, _) => Encoding.UTF8;
        if (additionalFormFields != null)
        {
            foreach (KeyValuePair<string, string> additionalFormField in additionalFormFields)
            {
                multipartFormDataContent.Add(new StringContent(additionalFormField.Value ?? string.Empty), additionalFormField.Key ?? string.Empty);
            }
        }
        using var byteArrayContent = new ByteArrayContent(LongPathFileSystem.ReadAllBytes(filePath));
        byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        byteArrayContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = QuoteContentDispositionValue(formFieldName),
            FileNameStar = null
        };
        // FileName プロパティの非 ASCII 名への MIME 符号化を避け、登録先が解釈する UTF-8 の filename を送る。
        byteArrayContent.Headers.ContentDisposition.Parameters.Add(
            new NameValueHeaderValue("filename", QuoteContentDispositionValue(fileName)));
        multipartFormDataContent.Add(byteArrayContent);
        using HttpResponseMessage httpResponseMessage = Send(HttpMethod.Post, uri, multipartFormDataContent, headers, throwOnNonSuccess: false);
        byte[] responseBytes = ReadResponseBytes(httpResponseMessage);
        string responseBody = DecodeStringAndTrimBom(responseBytes, responseEncoding);
        if (!httpResponseMessage.IsSuccessStatusCode)
        {
            string formattedResponseBody = logErrorResponseBody ? FormatResponseBodyForLog(responseBody) : null;
            LogRequestFailure(HttpMethod.Post.Method, uri, httpResponseMessage, null, null, formattedResponseBody);
            HttpStatusCode statusCode = httpResponseMessage.StatusCode;
            throw new HttpRequestException("HTTP request failed statusCode=" + (int)statusCode + " status=" + statusCode);
        }
        return responseBody;
    }

    /// <summary>
    /// 指定 URI の内容をファイルへ保存します。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="destinationPath">保存先ファイルパス。</param>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="destinationPath"/> が <see langword="null"/> の場合。</exception>
    internal void DownloadFile(Uri uri, string destinationPath)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (destinationPath == null)
        {
            throw new ArgumentNullException(nameof(destinationPath));
        }
        if (uri.IsFile)
        {
            LongPathFileSystem.CopyFile(uri.LocalPath, destinationPath, overwrite: true);
            return;
        }
        using HttpResponseMessage httpResponseMessage = Send(HttpMethod.Get, uri);
        using Stream stream = httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        using FileStream fileStream = LongPathFileSystem.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(fileStream);
    }

    /// <summary>
    /// 指定 URI の内容をストリームとして開き、応答メタデータと一緒に返します。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <returns>応答メタデータ付きストリーム。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal AppHttpResponse OpenRead(Uri uri)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (uri.IsFile)
        {
            return new AppHttpResponse(uri, LongPathFileSystem.OpenRead(uri.LocalPath));
        }
        HttpResponseMessage httpResponseMessage = Send(HttpMethod.Get, uri);
        try
        {
            Stream stream = httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return new AppHttpResponse(uri, httpResponseMessage, stream);
        }
        catch
        {
            httpResponseMessage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 指定 URI の内容を非同期にストリームとして開き、応答メタデータと一緒に返します。
    /// 大容量ファイル取得を UI 側から中断できるよう、HTTP リクエスト開始までのキャンセルを伝播します。
    /// ヘッダー待ちにはクライアント生成時の既定期限を使い、本文読取りは期限で切りません。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="cancellationToken">取得を中断するためのトークン。</param>
    /// <returns>応答メタデータ付きストリーム。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default)
        => OpenReadAsync(uri, defaultTimeout, cancellationToken);

    /// <summary>
    /// 指定 URI の内容を非同期にストリームとして開き、応答メタデータと一緒に返します。
    /// ヘッダー待ちだけをこの要求の <paramref name="headerTimeout"/> で切り、本文読取りは期限で切りません。
    /// 本文まで含めた期限は、呼出し側が <paramref name="cancellationToken"/> で与えます。
    /// 期限を指定した要求と指定しない要求が同時に動いても、互いの期限や共有クライアントの設定は変わりません。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="headerTimeout">応答ヘッダーを待つ期限。<see cref="Timeout.InfiniteTimeSpan"/> ではヘッダー待ちにも期限を付けません。</param>
    /// <param name="cancellationToken">取得を中断するためのトークン。</param>
    /// <returns>応答メタデータ付きストリーム。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="headerTimeout"/> が正でも無期限でもない場合。</exception>
    /// <exception cref="TaskCanceledException">ヘッダー待ちの期限を超えた場合。<see cref="TimeoutException"/> を内部例外に持ちます。</exception>
    internal async Task<AppHttpResponse> OpenReadAsync(Uri uri, TimeSpan headerTimeout, CancellationToken cancellationToken)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        TimeSpan timeout = ResolveTimeout(headerTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.IsFile)
        {
            return new AppHttpResponse(uri, LongPathFileSystem.OpenRead(uri.LocalPath));
        }
        HttpResponseMessage httpResponseMessage;
        using (var deadline = new RequestDeadline(timeProvider, timeout, cancellationToken))
        {
            httpResponseMessage = await SendAsync(HttpMethod.Get, uri, null, null, deadline).ConfigureAwait(false);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new AppHttpResponse(uri, httpResponseMessage, stream);
        }
        catch
        {
            httpResponseMessage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// リクエストを送信し、成功レスポンスだけを返します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="content">送信本文。</param>
    /// <param name="headers">追加のリクエストヘッダー。</param>
    /// <param name="throwOnNonSuccess"><see langword="true"/> の場合は非成功レスポンスを例外化します。</param>
    /// <returns>レスポンス。既定では成功レスポンスのみ返します。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> または <paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    private HttpResponseMessage Send(HttpMethod method, Uri uri, HttpContent content = null, IDictionary<string, string> headers = null, bool throwOnNonSuccess = true)
    {
        // ヘッダー待ちだけを既定期限で切り、同期呼出しの本文読取りは従来どおり期限で切りません。
        using var deadline = new RequestDeadline(timeProvider, defaultTimeout, CancellationToken.None);
        return SendAsync(method, uri, content, headers, deadline, throwOnNonSuccess).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// リクエストを非同期送信し、成功レスポンスだけを返します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="content">送信本文。</param>
    /// <param name="headers">追加のリクエストヘッダー。</param>
    /// <param name="deadline">この要求の期限と呼出し元の取消を結んだ送信取消。期限だけで取り消された場合は <see cref="TimeoutException"/> を内部例外に持つ <see cref="TaskCanceledException"/> にします。</param>
    /// <param name="throwOnNonSuccess"><see langword="true"/> の場合は非成功レスポンスを例外化します。</param>
    /// <returns>レスポンス。既定では成功レスポンスのみ返します。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> または <paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, HttpContent content, IDictionary<string, string> headers, RequestDeadline deadline, bool throwOnNonSuccess = true)
    {
        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        using var httpRequestMessage = new HttpRequestMessage(method, uri);
        httpRequestMessage.Content = content;
        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                httpRequestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        HttpResponseMessage httpResponseMessage;
        try
        {
            httpResponseMessage = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (deadline.TimedOut)
        {
            // HttpClient.Timeout が作る例外と同じ形にし、利用者取消と区別できるようにする。
            var timeout = new TaskCanceledException(
                "The request was canceled due to the configured request timeout of " + deadline.Timeout.TotalSeconds + " seconds elapsing.",
                new TimeoutException("The request timed out.", ex),
                deadline.Token);
            LogRequestFailure(method.Method, uri, null, "timeout", timeout);
            throw timeout;
        }
        catch (TaskCanceledException ex)
        {
            LogRequestFailure(method.Method, uri, null, "timeout_or_canceled", ex);
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogRequestFailure(method.Method, uri, null, "http_request_exception", ex);
            throw;
        }
        catch (Exception ex)
        {
            LogRequestFailure(method.Method, uri, null, "unexpected_exception", ex);
            throw;
        }
        if (!httpResponseMessage.IsSuccessStatusCode)
        {
            if (throwOnNonSuccess)
            {
                LogRequestFailure(method.Method, uri, httpResponseMessage, null, null);
                HttpStatusCode statusCode = httpResponseMessage.StatusCode;
                httpResponseMessage.Dispose();
                throw new HttpRequestException("HTTP request failed statusCode=" + (int)statusCode + " status=" + statusCode);
            }
            return httpResponseMessage;
        }
        LogRequestSuccess(method.Method, uri, httpResponseMessage);
        return httpResponseMessage;
    }

    /// <summary>
    /// バッファリングする HTTP 要求の期限と応答の寿命を、本文読み取りの実完了まで所有します。
    /// </summary>
    private async Task<byte[]> SendAndReadResponseBytesAsync(
        HttpMethod method,
        Uri uri,
        HttpContent content = null,
        IDictionary<string, string> headers = null,
        CancellationToken cancellationToken = default)
    {
        // 時計も期限も要求単位で所有し、ヘッダー待ちと同じ取消しを本文の実 I/O まで維持する。
        using var deadline = new RequestDeadline(timeProvider, defaultTimeout, cancellationToken);
        using HttpResponseMessage response = await SendAsync(method, uri, content, headers, deadline).ConfigureAwait(false);
        return await ReadResponseBytesAsync(response, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// レスポンス本文をバイト列として読み取ります。
    /// </summary>
    /// <param name="httpResponseMessage">読み取り対象のレスポンス。</param>
    /// <returns>レスポンス本文のバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpResponseMessage"/> が <see langword="null"/> の場合。</exception>
    private static byte[] ReadResponseBytes(HttpResponseMessage httpResponseMessage)
    {
        if (httpResponseMessage == null)
        {
            throw new ArgumentNullException(nameof(httpResponseMessage));
        }
        if (httpResponseMessage.Content == null)
        {
            return [];
        }
        return httpResponseMessage.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// レスポンス本文をキャンセル可能な非同期読み取りでバイト列として取得します。
    /// ResponseHeadersRead 経路では本文読み取りが別段階になるため、外部 API 呼び出しのキャンセルをここでも反映します。
    /// </summary>
    /// <param name="httpResponseMessage">読み取り対象のレスポンス。</param>
    /// <param name="cancellationToken">本文読み取りを中断するためのトークン。</param>
    /// <returns>レスポンス本文のバイト列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpResponseMessage"/> が <see langword="null"/> の場合。</exception>
    private static async Task<byte[]> ReadResponseBytesAsync(HttpResponseMessage httpResponseMessage, CancellationToken cancellationToken)
    {
        if (httpResponseMessage == null)
        {
            throw new ArgumentNullException(nameof(httpResponseMessage));
        }
        if (httpResponseMessage.Content == null)
        {
            return [];
        }
        using Stream responseStream = await httpResponseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memoryStream = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await memoryStream.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
        }
        return memoryStream.ToArray();
    }

    /// <summary>
    /// 一つの要求だけの期限を、クライアントが持つ時計で計時し、呼出し元の取消と結びます。
    /// 共有クライアントの設定を書き換えず、同時に動く要求の期限へ影響しません。
    /// </summary>
    private sealed class RequestDeadline : IDisposable
    {
        private readonly CancellationTokenSource timer;
        private readonly CancellationTokenSource linked;
        private readonly CancellationToken caller;

        internal RequestDeadline(TimeProvider timeProvider, TimeSpan timeout, CancellationToken caller)
        {
            Timeout = timeout;
            this.caller = caller;
            timer = new CancellationTokenSource(timeout, timeProvider);
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timer.Token);
        }

        /// <summary>この要求の期限です。</summary>
        internal TimeSpan Timeout { get; }

        /// <summary>期限と呼出し元の取消のどちらでも取り消される送信・本文読取り用トークンです。</summary>
        internal CancellationToken Token => linked.Token;

        /// <summary>呼出し元の取消ではなく、期限だけで取り消された場合に真です。</summary>
        internal bool TimedOut => timer.IsCancellationRequested && !caller.IsCancellationRequested;

        public void Dispose()
        {
            linked.Dispose();
            timer.Dispose();
        }
    }

    /// <summary>
    /// 未指定時に UTF-8 を返すよう文字コードを正規化します。
    /// </summary>
    /// <param name="encoding">利用したい文字コード。</param>
    /// <returns>有効な文字コード。</returns>
    private static Encoding ResolveEncoding(Encoding encoding)
    {
        return encoding ?? Encoding.UTF8;
    }

    /// <summary>
    /// バイト列を文字列化し、先頭の UTF-8 BOM を吸収します。
    /// 旧 <see cref="WebClient.DownloadString(Uri)"/> は BOM 付きの JSON/Text をそのまま扱えていたため、
    /// HttpClient 置き換え後も同等の互換性を保てるよう AppHttpClient 全体で先頭 BOM を除去します。
    /// </summary>
    /// <param name="bytes">文字列化するバイト列。</param>
    /// <param name="encoding">利用する文字コード。未指定時は UTF-8。</param>
    /// <returns>先頭 BOM を除去した文字列。</returns>
    private static string DecodeStringAndTrimBom(byte[] bytes, Encoding encoding)
    {
        string decoded = ResolveEncoding(encoding).GetString(bytes ?? []);
        if (!string.IsNullOrEmpty(decoded) && decoded[0] == '\uFEFF')
        {
            return decoded.Substring(1);
        }
        return decoded;
    }

    /// <summary>
    /// Content-Disposition 用の値を quoted-string へ正規化します。
    /// </summary>
    /// <param name="value">ヘッダーへ設定したい値。</param>
    /// <returns>quoted-string に変換した値。</returns>
    private static string QuoteContentDispositionValue(string value)
    {
        string normalizedValue = value ?? string.Empty;
        return "\"" + normalizedValue.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// ログ出力用にレスポンス本文を 1 行へ整形します。
    /// </summary>
    /// <param name="responseBody">整形対象の本文。</param>
    /// <returns>1 行に正規化されたログ用本文。空文字列相当なら <see langword="null"/>。</returns>
    private static string FormatResponseBodyForLog(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }
        string singleLineBody = responseBody.Trim().Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
        const int maxLogLength = 2048;
        if (singleLineBody.Length > maxLogLength)
        {
            return singleLineBody.Substring(0, maxLogLength) + "...(truncated)";
        }
        return singleLineBody;
    }

    /// <summary>
    /// Content-Type 文字列から本文送信時の文字コードを解決します。
    /// </summary>
    /// <param name="contentType">送信時の Content-Type。</param>
    /// <returns>本文送信に使う文字コード。</returns>
    private static Encoding ResolveContentEncoding(string contentType)
    {
        try
        {
            string charSet = MediaTypeHeaderValue.Parse(contentType)?.CharSet;
            if (!string.IsNullOrWhiteSpace(charSet))
            {
                return Encoding.GetEncoding(charSet.Trim('"'));
            }
        }
        catch
        {
        }
        return Encoding.UTF8;
    }

    /// <summary>
    /// 既定の User-Agent を構築します。
    /// </summary>
    /// <returns>アプリ名とバージョンを含む User-Agent。</returns>
    private static string BuildDefaultUserAgent()
    {
        try
        {
            Version version = Assembly.GetEntryAssembly()?.GetName()?.Version ?? Assembly.GetExecutingAssembly()?.GetName()?.Version;
            if (version != null)
            {
                return "BeMusicSeeker/" + version;
            }
        }
        catch
        {
        }
        return "BeMusicSeeker/unknown";
    }

    /// <summary>
    /// 成功した HTTP 応答を INFO ログへ出力します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">対象 URI。</param>
    /// <param name="httpResponseMessage">成功レスポンス。</param>
    private static void LogRequestSuccess(string method, Uri uri, HttpResponseMessage httpResponseMessage)
    {
        try
        {
            if (httpResponseMessage != null)
            {
                NLogWrapper.FileLogger?.Info("http_request_succeeded method=" + method + " url=" + uri + " statusCode=" + (int)httpResponseMessage.StatusCode + " status=" + httpResponseMessage.StatusCode);
            }
            else
            {
                NLogWrapper.FileLogger?.Info("http_request_succeeded method=" + method + " url=" + uri);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 失敗した HTTP 応答または送信例外を WARN ログへ出力します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">対象 URI。</param>
    /// <param name="httpResponseMessage">失敗レスポンス。例外送出前の送信失敗時は <see langword="null"/>。</param>
    /// <param name="reason">任意の失敗理由。</param>
    /// <param name="exception">送信時例外。</param>
    /// <param name="responseBody">レスポンス本文のログ出力用文字列。</param>
    private static void LogRequestFailure(string method, Uri uri, HttpResponseMessage httpResponseMessage, string reason, Exception exception, string responseBody = null)
    {
        try
        {
            string message = "http_request_failed method=" + method + " url=" + uri;
            if (httpResponseMessage != null)
            {
                message = message + " statusCode=" + (int)httpResponseMessage.StatusCode + " status=" + httpResponseMessage.StatusCode;
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                message = message + " reason=" + reason;
            }
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                message = message + " responseBody=" + responseBody;
            }
            if (exception != null)
            {
                NLogWrapper.FileLogger?.Warn(exception, message);
            }
            else
            {
                NLogWrapper.FileLogger?.Warn(message);
            }
        }
        catch
        {
        }
    }
}
