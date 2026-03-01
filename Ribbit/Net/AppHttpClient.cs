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
        if (requestUri == null)
        {
            throw new ArgumentNullException(nameof(requestUri));
        }
        if (httpResponseMessage == null)
        {
            throw new ArgumentNullException(nameof(httpResponseMessage));
        }
        if (responseStream == null)
        {
            throw new ArgumentNullException(nameof(responseStream));
        }
        RequestUri = requestUri;
        ResponseUri = httpResponseMessage.RequestMessage?.RequestUri ?? requestUri;
        StatusCode = httpResponseMessage.StatusCode;
        Headers = httpResponseMessage.Headers;
        ContentHeaders = httpResponseMessage.Content?.Headers;
        ContentLength = httpResponseMessage.Content?.Headers?.ContentLength;
        ResponseStream = responseStream;
        this.httpResponseMessage = httpResponseMessage;
    }

    /// <summary>
    /// ローカルファイル読み取り用のハンドルを初期化します。
    /// </summary>
    /// <param name="fileUri">対象ファイル URI。</param>
    /// <param name="responseStream">保持するファイルストリーム。</param>
    internal AppHttpResponse(Uri fileUri, Stream responseStream)
    {
        if (fileUri == null)
        {
            throw new ArgumentNullException(nameof(fileUri));
        }
        if (responseStream == null)
        {
            throw new ArgumentNullException(nameof(responseStream));
        }
        RequestUri = fileUri;
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
    /// </summary>
    private readonly HttpClient httpClient;

    /// <summary>
    /// 指定タイムアウトで HTTP クライアントを初期化します。
    /// </summary>
    /// <param name="requestTimeoutMs">リクエスト全体のタイムアウト時間（ミリ秒）。</param>
    private AppHttpClient(int requestTimeoutMs)
    {
        HttpClientHandler httpClientHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        httpClient = new HttpClient(httpClientHandler)
        {
            Timeout = TimeSpan.FromMilliseconds((requestTimeoutMs > 0) ? requestTimeoutMs : DefaultTimeoutMs)
        };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
    }

    /// <summary>
    /// 指定タイムアウトを持つクライアントを生成します。
    /// </summary>
    /// <param name="requestTimeoutMs">リクエスト全体のタイムアウト時間（ミリ秒）。</param>
    /// <returns>指定タイムアウトで構成された <see cref="AppHttpClient"/>。</returns>
    internal static AppHttpClient Create(int requestTimeoutMs)
    {
        return new AppHttpClient(requestTimeoutMs);
    }

    /// <summary>
    /// 指定 URI のバイト列を取得します。
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
            return File.ReadAllBytes(uri.LocalPath);
        }
        using (HttpResponseMessage httpResponseMessage = Send(HttpMethod.Get, uri))
        {
            return httpResponseMessage.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
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
        return ResolveEncoding(encoding).GetString(GetBytes(uri));
    }

    /// <summary>
    /// 指定 URI の内容を非同期に文字列として取得します。
    /// </summary>
    /// <param name="uri">取得元 URI。</param>
    /// <param name="encoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>指定文字コードで解釈した文字列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    internal async Task<string> GetStringAsync(Uri uri, Encoding encoding = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (uri.IsFile)
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(uri.LocalPath), cancellationToken).ConfigureAwait(false);
            return ResolveEncoding(encoding).GetString(bytes);
        }
        using (HttpResponseMessage httpResponseMessage = await SendAsync(HttpMethod.Get, uri, null, null, cancellationToken).ConfigureAwait(false))
        {
            byte[] bytes = await httpResponseMessage.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return ResolveEncoding(encoding).GetString(bytes);
        }
    }

    /// <summary>
    /// 指定 URI へフォームエンコードされた POST を送信します。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="formData">送信するフォーム値。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="formData"/> が <see langword="null"/> の場合。</exception>
    internal string PostForm(Uri uri, NameValueCollection formData, Encoding responseEncoding = null)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (formData == null)
        {
            throw new ArgumentNullException(nameof(formData));
        }
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
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
        using (FormUrlEncodedContent formUrlEncodedContent = new FormUrlEncodedContent(list))
        {
            return ReadResponseString(Send(HttpMethod.Post, uri, formUrlEncodedContent), responseEncoding);
        }
    }

    /// <summary>
    /// 指定 URI へ文字列本文を POST します。
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
        using (ByteArrayContent byteArrayContent = new ByteArrayContent(bytes))
        {
            byteArrayContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return ReadResponseString(Send(HttpMethod.Post, uri, byteArrayContent, headers), responseEncoding);
        }
    }

    /// <summary>
    /// 指定ファイルを multipart/form-data で POST します。
    /// </summary>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="filePath">送信するファイルパス。</param>
    /// <param name="formFieldName">multipart のフィールド名。省略時は <c>file</c>。</param>
    /// <param name="fileName">送信時のファイル名。省略時は元のファイル名。</param>
    /// <param name="additionalFormFields">追加のフォームフィールド。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> または <paramref name="filePath"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="FileNotFoundException"><paramref name="filePath"/> が存在しない場合。</exception>
    internal string PostFile(Uri uri, string filePath, string formFieldName = "file", string fileName = null, IDictionary<string, string> additionalFormFields = null, Encoding responseEncoding = null)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        if (filePath == null)
        {
            throw new ArgumentNullException(nameof(filePath));
        }
        if (!File.Exists(filePath))
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
        using (MultipartFormDataContent multipartFormDataContent = new MultipartFormDataContent())
        {
            if (additionalFormFields != null)
            {
                foreach (KeyValuePair<string, string> additionalFormField in additionalFormFields)
                {
                    multipartFormDataContent.Add(new StringContent(additionalFormField.Value ?? string.Empty), additionalFormField.Key ?? string.Empty);
                }
            }
            using (FileStream fileStream = File.OpenRead(filePath))
            {
                using (StreamContent streamContent = new StreamContent(fileStream))
                {
                    multipartFormDataContent.Add(streamContent, formFieldName, fileName);
                    return ReadResponseString(Send(HttpMethod.Post, uri, multipartFormDataContent), responseEncoding);
                }
            }
        }
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
            File.Copy(uri.LocalPath, destinationPath, overwrite: true);
            return;
        }
        using (HttpResponseMessage httpResponseMessage = Send(HttpMethod.Get, uri))
        {
            using (Stream stream = httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false).GetAwaiter().GetResult())
            {
                using (FileStream fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }
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
            return new AppHttpResponse(uri, File.OpenRead(uri.LocalPath));
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
    /// リクエストを送信し、成功レスポンスだけを返します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="content">送信本文。</param>
    /// <param name="headers">追加のリクエストヘッダー。</param>
    /// <returns>成功レスポンス。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> または <paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    private HttpResponseMessage Send(HttpMethod method, Uri uri, HttpContent content = null, IDictionary<string, string> headers = null)
    {
        return SendAsync(method, uri, content, headers, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// リクエストを非同期送信し、成功レスポンスだけを返します。
    /// </summary>
    /// <param name="method">HTTP メソッド。</param>
    /// <param name="uri">送信先 URI。</param>
    /// <param name="content">送信本文。</param>
    /// <param name="headers">追加のリクエストヘッダー。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>成功レスポンス。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> または <paramref name="uri"/> が <see langword="null"/> の場合。</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, HttpContent content = null, IDictionary<string, string> headers = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }
        using (HttpRequestMessage httpRequestMessage = new HttpRequestMessage(method, uri))
        {
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
                httpResponseMessage = await httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
                LogRequestFailure(method.Method, uri, httpResponseMessage, null, null);
                HttpStatusCode statusCode = httpResponseMessage.StatusCode;
                httpResponseMessage.Dispose();
                throw new HttpRequestException("HTTP request failed statusCode=" + (int)statusCode + " status=" + statusCode);
            }
            LogRequestSuccess(method.Method, uri, httpResponseMessage);
            return httpResponseMessage;
        }
    }

    /// <summary>
    /// レスポンス本文を文字列として読み取ります。
    /// </summary>
    /// <param name="httpResponseMessage">読み取り対象のレスポンス。</param>
    /// <param name="responseEncoding">レスポンスを文字列化する文字コード。省略時は UTF-8。</param>
    /// <returns>レスポンス本文。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpResponseMessage"/> が <see langword="null"/> の場合。</exception>
    private static string ReadResponseString(HttpResponseMessage httpResponseMessage, Encoding responseEncoding)
    {
        if (httpResponseMessage == null)
        {
            throw new ArgumentNullException(nameof(httpResponseMessage));
        }
        using (httpResponseMessage)
        {
            byte[] bytes = httpResponseMessage.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            return ResolveEncoding(responseEncoding).GetString(bytes);
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
    private static void LogRequestFailure(string method, Uri uri, HttpResponseMessage httpResponseMessage, string reason, Exception exception)
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
