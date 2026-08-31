using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models.Utils;
using Ribbit.Net;

namespace BeMusicSeeker.Models;

internal enum PlaylistUrlDownloadResultKind
{
    Downloaded,
    BrowserFallback,
    BlockedBySizeLimit,
    Duplicate,
    Failed
}

internal sealed class PlaylistUrlDownloadResult
{
    private PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind kind, string filePath = null, string downloadKey = null, bool isUnsupportedScheme = false)
    {
        Kind = kind;
        FilePath = filePath ?? string.Empty;
        DownloadKey = downloadKey ?? string.Empty;
        IsUnsupportedScheme = isUnsupportedScheme;
    }

    internal PlaylistUrlDownloadResultKind Kind { get; }

    internal string FilePath { get; }

    internal string DownloadKey { get; }

    /// <summary>
    /// Gets whether a failed acquisition was caused by a URI scheme rejected at an ingress boundary.
    /// </summary>
    internal bool IsUnsupportedScheme { get; }

    internal static PlaylistUrlDownloadResult Downloaded(string filePath, string downloadKey)
    {
        return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Downloaded, filePath, downloadKey);
    }

    internal static PlaylistUrlDownloadResult BrowserFallback(string downloadKey = null)
    {
        return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.BrowserFallback, downloadKey: downloadKey);
    }

    internal static PlaylistUrlDownloadResult BlockedBySizeLimit(string downloadKey = null)
    {
        return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.BlockedBySizeLimit, downloadKey: downloadKey);
    }

    internal static PlaylistUrlDownloadResult Duplicate(string downloadKey)
    {
        return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Duplicate, downloadKey: downloadKey);
    }

    internal static PlaylistUrlDownloadResult Failed(string downloadKey = null, bool isUnsupportedScheme = false)
    {
        return new PlaylistUrlDownloadResult(PlaylistUrlDownloadResultKind.Failed, downloadKey: downloadKey, isUnsupportedScheme: isUnsupportedScheme);
    }
}

internal interface IPlaylistUrlDownloadGateway
{
    Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken);
    string GetTemporaryDirectory();
    FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share);
    bool FileExists(string path);
    void DeleteFile(string path);
}

internal sealed class AppPlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
{
    public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
    {
        return AppHttpClient.Shared.OpenReadAsync(uri, cancellationToken);
    }

    public string GetTemporaryDirectory()
    {
        return TempDirectoryPublisher.Get();
    }

    public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share)
    {
        return LongPathFileSystem.Open(path, mode, access, share);
    }

    public bool FileExists(string path)
    {
        return LongPathFileSystem.FileExists(path);
    }

    public void DeleteFile(string path)
    {
        LongPathFileSystem.DeleteFile(path);
    }
}

internal sealed class PlaylistUrlAcquisitionWorkflow
{
    private const long DownloadAndInstallSizeLimitBytes = 536870912L;

    private const int SharedDownloadPageResolverMaxBytes = 2097152;

    private static readonly string[] DownloadAndInstallArchiveExtensions = [".zip", ".7z", ".rar", ".lzh"];

    private static readonly Regex dropBoxRegex = new("https?://(?:(?:www|dl)\\.dropbox\\.com|dl\\.dropboxusercontent\\.com)/(sh?)/([^?]*)\\.([^?]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex gdriveRegex = new("https?://drive\\.google\\.com/(file/d/|open\\?id=)([^/]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex odriveRegex = new("https?://onedrive\\.live\\.com/redir\\?(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex htmlAttributeRegex = new("(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\\s*=\\s*(?:\\\"(?<double>[^\\\"]*)\\\"|'(?<single>[^']*)'|(?<bare>[^\\s\\\"'=<>`]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IPlaylistUrlDownloadGateway _downloadGateway;

    private readonly Action<string> log;

    internal PlaylistUrlAcquisitionWorkflow(
        IPlaylistUrlDownloadGateway downloadGateway,
        Action<string> log)
    {
        _downloadGateway = downloadGateway ?? throw new ArgumentNullException(nameof(downloadGateway));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool IsStagedFileReady(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && _downloadGateway.FileExists(path);
    }

    internal static string CreatePlaylistUrlDownloadKey(Uri uri)
    {
        return CreateDownloadKeyCore(uri);
    }
    internal async Task<PlaylistUrlDownloadResult> DownloadCandidateAsync(Uri uri, HashSet<string> downloadedKeys = null, bool allowSharedPageResolution = true, CancellationToken cancellationToken = default)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return PlaylistUrlDownloadResult.BrowserFallback();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHttpOrHttps(uri))
        {
            LogDownload("playlist_url_download rejected_unsupported_scheme source=" + uri);
            return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(uri), isUnsupportedScheme: true);
        }
        Uri normalizedUri = NormalizeDownloadUri(uri);
        if (!IsHttpOrHttps(normalizedUri))
        {
            LogDownload("playlist_url_download rejected_unsupported_scheme source=" + uri + " normalized=" + normalizedUri);
            return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(normalizedUri), isUnsupportedScheme: true);
        }
        if (IsBrowserFallbackDownloadUri(normalizedUri))
        {
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(normalizedUri));
        }
        string tempDirectory = _downloadGateway.GetTemporaryDirectory();
        try
        {
            using AppHttpResponse response = await _downloadGateway.OpenReadAsync(normalizedUri, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await DownloadPlaylistUrlResponseCandidateAsync(normalizedUri, response, tempDirectory, allowSharedPageResolution, downloadedKeys, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDownload("playlist_url_download failed source=" + uri + " normalized=" + normalizedUri + " errorType=" + ex.GetType().FullName + " error=" + SanitizeDownloadLogValue(ex.Message));
            return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(normalizedUri));
        }
    }

    private Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlResponseCandidateAsync(Uri requestedUri, AppHttpResponse response, string tempDirectory, bool allowSharedPageResolution, HashSet<string> downloadedKeys = null, CancellationToken cancellationToken = default)
    {
        return DownloadPlaylistUrlResponseCandidateAsync(requestedUri, response, tempDirectory, allowSharedPageResolution ? 4 : 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), downloadedKeys, cancellationToken);
    }

    private async Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlResponseCandidateAsync(Uri requestedUri, AppHttpResponse response, string tempDirectory, int remainingSharedPageResolutionDepth, HashSet<string> resolvedPageUris, HashSet<string> downloadedKeys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri responseUri = response?.ResponseUri;
        if (!IsHttpOrHttps(requestedUri) || !IsHttpOrHttps(responseUri))
        {
            LogDownload("playlist_url_download rejected_unsupported_response_scheme requested=" + requestedUri + " response=" + (responseUri?.ToString() ?? string.Empty));
            return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(responseUri ?? requestedUri), isUnsupportedScheme: true);
        }
        AddUriWithoutFragment(resolvedPageUris, requestedUri);
        AddUriWithoutFragment(resolvedPageUris, responseUri);
        if (response.ContentLength.HasValue)
        {
            if (response.ContentLength.Value == 0L)
            {
                return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
            }
            if (response.ContentLength.Value > DownloadAndInstallSizeLimitBytes)
            {
                LogDownload("playlist_url_download blocked_size_limit source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " contentLength=" + response.ContentLength.Value + " limitBytes=" + DownloadAndInstallSizeLimitBytes);
                return PlaylistUrlDownloadResult.BlockedBySizeLimit(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
            }
        }
        if (remainingSharedPageResolutionDepth > 0 && ShouldResolveSharedDownloadPageBeforeFileName(requestedUri, response))
        {
            (Uri resolvedUri, bool unsupportedScheme) resolution = await TryResolveSharedDownloadPageUriAsync(requestedUri, response, cancellationToken).ConfigureAwait(false);
            if (resolution.unsupportedScheme)
            {
                LogDownload("playlist_url_download rejected_unsupported_recursive_scheme source=" + requestedUri + " response=" + responseUri);
                return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(responseUri ?? requestedUri), isUnsupportedScheme: true);
            }
            Uri resolvedUri = resolution.resolvedUri;
            if (resolvedUri != null && AddUriWithoutFragment(resolvedPageUris, resolvedUri))
            {
                string resolvedDownloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(resolvedDownloadKey) && downloadedKeys != null && downloadedKeys.Contains(resolvedDownloadKey))
                {
                    LogDownload("playlist_url_download duplicate source=" + requestedUri + " resolved=" + resolvedUri + " key=" + resolvedDownloadKey);
                    return PlaylistUrlDownloadResult.Duplicate(resolvedDownloadKey);
                }
                LogDownload("playlist_url_download resolved source=" + requestedUri + " resolved=" + resolvedUri + " depth=" + remainingSharedPageResolutionDepth);
                using AppHttpResponse resolvedResponse = await _downloadGateway.OpenReadAsync(resolvedUri, cancellationToken).ConfigureAwait(false);
                return await DownloadPlaylistUrlResponseCandidateAsync(resolvedUri, resolvedResponse, tempDirectory, remainingSharedPageResolutionDepth - 1, resolvedPageUris, downloadedKeys, cancellationToken).ConfigureAwait(false);
            }
            LogDownload("playlist_url_download unresolved_shared_page source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " contentType=" + GetContentTypeLogValue(response));
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        string fileName = ResolveDownloadedArchiveFileName(requestedUri, response);
        if (IsHtmlContentType(response))
        {
            LogDownload("playlist_url_download skipped_html source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " fileName=" + fileName);
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        if (!IsDownloadAndInstallCandidateFileName(fileName))
        {
            (Uri resolvedUri, bool unsupportedScheme) resolution = remainingSharedPageResolutionDepth > 0
                ? await TryResolveSharedDownloadPageUriAsync(requestedUri, response, cancellationToken).ConfigureAwait(false)
                : (null, false);
            if (resolution.unsupportedScheme)
            {
                LogDownload("playlist_url_download rejected_unsupported_recursive_scheme source=" + requestedUri + " response=" + responseUri);
                return PlaylistUrlDownloadResult.Failed(CreatePlaylistUrlDownloadKey(responseUri ?? requestedUri), isUnsupportedScheme: true);
            }
            Uri resolvedUri = resolution.resolvedUri;
            if (resolvedUri != null && AddUriWithoutFragment(resolvedPageUris, resolvedUri))
            {
                string resolvedDownloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(resolvedDownloadKey) && downloadedKeys != null && downloadedKeys.Contains(resolvedDownloadKey))
                {
                    LogDownload("playlist_url_download duplicate source=" + requestedUri + " resolved=" + resolvedUri + " key=" + resolvedDownloadKey);
                    return PlaylistUrlDownloadResult.Duplicate(resolvedDownloadKey);
                }
                LogDownload("playlist_url_download resolved source=" + requestedUri + " resolved=" + resolvedUri + " depth=" + remainingSharedPageResolutionDepth);
                using AppHttpResponse resolvedResponse = await _downloadGateway.OpenReadAsync(resolvedUri, cancellationToken).ConfigureAwait(false);
                return await DownloadPlaylistUrlResponseCandidateAsync(resolvedUri, resolvedResponse, tempDirectory, remainingSharedPageResolutionDepth - 1, resolvedPageUris, downloadedKeys, cancellationToken).ConfigureAwait(false);
            }
            LogDownload("playlist_url_download skipped_unsupported source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " fileName=" + fileName + " contentType=" + GetContentTypeLogValue(response));
            return PlaylistUrlDownloadResult.BrowserFallback(CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri));
        }
        string downloadKey = CreatePlaylistUrlDownloadKey(response?.ResponseUri ?? requestedUri);
        if (!string.IsNullOrWhiteSpace(downloadKey) && downloadedKeys != null)
        {
            if (downloadedKeys.Contains(downloadKey))
            {
                LogDownload("playlist_url_download duplicate source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " key=" + downloadKey);
                return PlaylistUrlDownloadResult.Duplicate(downloadKey);
            }
        }
        string filePath = Path.Combine(tempDirectory, fileName);
        if (!await TryCopyStreamToFileWithLimitAsync(response.ResponseStream, filePath, DownloadAndInstallSizeLimitBytes, cancellationToken).ConfigureAwait(false))
        {
            LogDownload("playlist_url_download blocked_size_limit source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " file=" + fileName + " limitBytes=" + DownloadAndInstallSizeLimitBytes);
            return PlaylistUrlDownloadResult.BlockedBySizeLimit(downloadKey);
        }
        if (!string.IsNullOrWhiteSpace(downloadKey))
        {
            downloadedKeys?.Add(downloadKey);
        }
        LogDownload("playlist_url_download downloaded source=" + requestedUri + " response=" + (response?.ResponseUri?.ToString() ?? string.Empty) + " key=" + (downloadKey ?? string.Empty) + " file=" + fileName + " contentType=" + GetContentTypeLogValue(response));
        return PlaylistUrlDownloadResult.Downloaded(filePath, downloadKey);
    }

    private static bool ShouldResolveSharedDownloadPageBeforeFileName(Uri requestedUri, AppHttpResponse response)
    {
        Uri responseUri = response?.ResponseUri;
        if (!IsSharedDownloadPageResolutionCandidate(requestedUri) && !IsSharedDownloadPageResolutionCandidate(responseUri))
        {
            return false;
        }
        return IsHtmlContentType(response)
            || IsDownloadSourcePageUri(requestedUri)
            || IsDownloadSourcePageUri(responseUri)
            || IsKnownDownloadLandingPageResponseUri(requestedUri)
            || IsKnownDownloadLandingPageResponseUri(responseUri);
    }

    private static bool IsKnownDownloadLandingPageResponseUri(Uri uri)
    {
        return IsMediaFireLandingPageUri(uri);
    }

    private static bool IsHtmlContentType(AppHttpResponse response)
    {
        string mediaType = response?.ContentHeaders?.ContentType?.MediaType;
        return mediaType != null
            && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetContentTypeLogValue(AppHttpResponse response)
    {
        return response?.ContentHeaders?.ContentType?.ToString() ?? string.Empty;
    }

    internal void LogDownload(string message)
    {
        log(message);
    }

    internal static string SanitizeDownloadLogValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static string CreateDownloadKeyCore(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return string.Empty;
        }
        Uri normalizedUri = NormalizeDownloadUri(uri);
        string googleDriveFileId = ExtractGoogleDriveFileId(normalizedUri);
        if (!string.IsNullOrWhiteSpace(googleDriveFileId) && IsGoogleDriveHost(normalizedUri))
        {
            return "gdrive:" + googleDriveFileId;
        }
        string mediaFireFileId = ExtractMediaFireFileId(normalizedUri);
        if (!string.IsNullOrWhiteSpace(mediaFireFileId))
        {
            return "mediafire:" + mediaFireFileId;
        }
        var builder = new UriBuilder(normalizedUri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ExtractMediaFireFileId(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsExactHostOrSubdomain(uri.Host, "mediafire.com"))
        {
            return null;
        }
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[1]);
        }
        if (segments.Length >= 2 && (uri.Host ?? string.Empty).StartsWith("download", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[segments.Length - 2]);
        }
        return null;
    }

    internal static bool IsBrowserFallbackDownloadUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return true;
        }
        if (IsSharedDownloadPageResolutionCandidate(uri))
        {
            return false;
        }
        string urlText = uri.ToString();
        return urlText.EndsWith("/", StringComparison.OrdinalIgnoreCase)
            || urlText.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || urlText.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDownloadAndInstallCandidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        if (ChartFileKindResolver.IsSupportedChartFilePath(fileName))
        {
            return true;
        }
        return DownloadAndInstallArchiveExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDownloadUrlString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }
        string text = NormalizeDropboxDownloadUrlString(input);
        text = NormalizeGoogleDriveDownloadUrlString(text);
        text = odriveRegex.Replace(text, "https://onedrive.live.com/download?$1");
        return text;
    }

    private static string NormalizeDropboxDownloadUrlString(string input)
    {
        string text = dropBoxRegex.Replace(input, "https://dl.dropboxusercontent.com/$1/$2.$3");
        if (!string.Equals(text, input, StringComparison.Ordinal))
        {
            return text;
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
        {
            return input;
        }
        string host = uri.Host ?? string.Empty;
        if (!IsExactHostOrSubdomain(host, "dropbox.com")
            || !uri.AbsolutePath.StartsWith("/scl/fi/", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }
        return SetUriQueryParameter(uri, "dl", "1").ToString();
    }

    private static string NormalizeGoogleDriveDownloadUrlString(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
        {
            return gdriveRegex.Replace(input, "https://drive.usercontent.google.com/download?id=$2&export=download");
        }
        string host = uri.Host ?? string.Empty;
        if (host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            string fileId = ExtractGoogleDriveFileId(uri);
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return BuildGoogleDriveDownloadUri(fileId).ToString();
            }
        }
        if (host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase))
        {
            string fileId = GetQueryParameter(uri, "id");
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return BuildGoogleDriveDownloadUri(fileId).ToString();
            }
        }
        if (host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return SetUriQueryParameter(uri, "export", "download").ToString();
        }
        return input;
    }

    internal static Uri NormalizeDownloadUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return uri;
        }
        try
        {
            return new Uri(NormalizeDownloadUrlString(uri.ToString()), UriKind.Absolute);
        }
        catch
        {
            return uri;
        }
    }

    private static Uri BuildGoogleDriveDownloadUri(string fileId)
    {
        var builder = new UriBuilder("https://drive.usercontent.google.com/download");
        builder.Query = "id=" + Uri.EscapeDataString(fileId) + "&export=download";
        return builder.Uri;
    }

    private static string ExtractGoogleDriveFileId(Uri uri)
    {
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i].Equals("file", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[i + 2]);
            }
        }
        string fileId = GetQueryParameter(uri, "id");
        return string.IsNullOrWhiteSpace(fileId) ? null : fileId;
    }

    private static async Task<(Uri resolvedUri, bool unsupportedScheme)> TryResolveSharedDownloadPageUriAsync(Uri requestedUri, AppHttpResponse response, CancellationToken cancellationToken = default)
    {
        if (!IsSharedDownloadPageResolutionCandidate(requestedUri) && !IsSharedDownloadPageResolutionCandidate(response?.ResponseUri))
        {
            return (null, false);
        }
        if (response?.ContentLength > SharedDownloadPageResolverMaxBytes)
        {
            return (null, false);
        }
        string html = await ReadTextWithLimitAsync(response?.ResponseStream, response?.ContentHeaders?.ContentType?.CharSet, SharedDownloadPageResolverMaxBytes, cancellationToken).ConfigureAwait(false);
        if (html == null)
        {
            return (null, false);
        }
        Uri resolvedUri = ResolveSharedDownloadPageUri(response.ResponseUri ?? requestedUri, html);
        if (resolvedUri != null && resolvedUri.IsAbsoluteUri && !IsSameUriWithoutFragment(resolvedUri, requestedUri))
        {
            return IsHttpOrHttps(resolvedUri)
                ? (resolvedUri, false)
                : (null, true);
        }
        return ContainsUnsupportedSharedDownloadUri(response.ResponseUri ?? requestedUri, html)
            ? (null, true)
            : (null, false);
    }

    /// <summary>
    /// Determines whether a URL acquisition hop is permitted to use the HTTP transport.
    /// </summary>
    /// <param name="uri">The URI to validate.</param>
    /// <returns><see langword="true"/> when <paramref name="uri"/> uses HTTP or HTTPS.</returns>
    internal static bool IsHttpOrHttpsUri(Uri uri)
    {
        return IsHttpOrHttps(uri);
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return uri != null && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsUnsupportedSharedDownloadUri(Uri pageUri, string html)
    {
        if (!IsSharedDownloadPageResolutionCandidate(pageUri) || string.IsNullOrWhiteSpace(html))
        {
            return false;
        }
        bool isMediaFirePage = IsExactHostOrSubdomain(pageUri.Host, "mediafire.com");
        bool isDownloadSourcePage = IsDownloadSourcePageUri(pageUri);
        foreach (Match anchorMatch in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(anchorMatch.Value);
            if (!attributes.TryGetValue("href", out string href)
                || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }
            if (isMediaFirePage)
            {
                attributes.TryGetValue("id", out string id);
                attributes.TryGetValue("class", out string className);
                if (!string.Equals(id, "downloadButton", StringComparison.OrdinalIgnoreCase)
                    && !(className?.IndexOf("popsok", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }
                if (IsUnsupportedAbsoluteDownloadUri(pageUri, href))
                {
                    return true;
                }
                continue;
            }
            if (isDownloadSourcePage
                && IsUnsupportedAbsoluteDownloadUri(pageUri, href))
            {
                return true;
            }
        }
        foreach (Match formMatch in Regex.Matches(html, "<form\\b(?=[^>]*\\bid\\s*=\\s*[\"']download-form[\"'])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(formMatch.Value);
            if (attributes.TryGetValue("action", out string action)
                && IsUnsupportedAbsoluteDownloadUri(pageUri, action))
            {
                return true;
            }
        }
        string decodedJson = DecodeEmbeddedJsonText(html);
        foreach (Match uriMatch in Regex.Matches(
            decodedJson,
            "\"downloadURL\"\\s*:\\s*\"(?<url>[^\"<>]+)\"",
            RegexOptions.IgnoreCase))
        {
            if (IsUnsupportedAbsoluteDownloadUri(pageUri, uriMatch.Groups["url"].Value))
            {
                return true;
            }
        }
        foreach (Match blockMatch in Regex.Matches(decodedJson, "\"downloads\"\\s*:\\s*\\[(?<body>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            foreach (Match uriMatch in Regex.Matches(
                blockMatch.Groups["body"].Value,
                "\"url\"\\s*:\\s*\"(?<url>[^\"<>]+)\"",
                RegexOptions.IgnoreCase))
            {
                if (IsUnsupportedAbsoluteDownloadUri(pageUri, uriMatch.Groups["url"].Value))
                {
                    return true;
                }
            }
        }
        foreach (Match downloadMatch in Regex.Matches(html, "(?:Down\\s*Load|Download)Address.*?<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(downloadMatch.Value);
            if (attributes.TryGetValue("href", out string href)
                && IsUnsupportedAbsoluteDownloadUri(pageUri, href))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsUnsupportedAbsoluteDownloadUri(Uri pageUri, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return false;
        }
        string decodedCandidate = WebUtility.HtmlDecode(candidateText.Trim());
        if (!Uri.TryCreate(pageUri, decodedCandidate, out Uri candidate))
        {
            return false;
        }
        if (!candidate.IsAbsoluteUri || IsHttpOrHttps(candidate))
        {
            return false;
        }
        return true;
    }

    private static bool IsExactHostOrSubdomain(string host, string rootDomain)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(rootDomain))
        {
            return false;
        }
        return host.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSharedDownloadPageResolutionCandidate(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            || IsManbowDownloadPageUri(uri)
            || IsVenueBmsSearchUri(uri)
            || IsBmsSearchInfoUri(uri)
            || host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || IsExactHostOrSubdomain(host, "mediafire.com");
    }

    internal static Uri ResolveSharedDownloadPageUri(Uri pageUri, string html)
    {
        if (pageUri == null || string.IsNullOrWhiteSpace(html))
        {
            return null;
        }
        if (TryResolveGoogleDriveWarningPageUri(pageUri, html, out Uri googleDriveUri))
        {
            return googleDriveUri;
        }
        if (TryResolveMediaFireDownloadUri(pageUri, html, out Uri mediaFireUri))
        {
            return mediaFireUri;
        }
        if (TryResolveManbowDownloadUri(pageUri, html, out Uri manbowUri))
        {
            return manbowUri;
        }
        if (TryResolveBmsSearchDownloadUri(pageUri, html, out Uri bmsSearchUri))
        {
            return bmsSearchUri;
        }
        return null;
    }

    private static bool TryResolveGoogleDriveWarningPageUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsGoogleDriveHost(pageUri))
        {
            return false;
        }
        Match formMatch = Regex.Match(html, "<form\\b(?=[^>]*\\bid\\s*=\\s*[\"']download-form[\"'])[^>]*>(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!formMatch.Success)
        {
            return false;
        }
        Dictionary<string, string> formAttributes = ParseHtmlAttributes(formMatch.Value);
        if (formAttributes.TryGetValue("method", out string method) && !string.Equals(method, "get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!formAttributes.TryGetValue("action", out string action) || string.IsNullOrWhiteSpace(action))
        {
            action = pageUri.ToString();
        }
        if (!Uri.TryCreate(pageUri, action, out Uri actionUri))
        {
            return false;
        }
        if (!IsHttpOrHttps(actionUri) || !IsGoogleDriveHost(actionUri))
        {
            return false;
        }
        var queryParameters = ParseQueryParameters(actionUri.Query);
        foreach (Match inputMatch in Regex.Matches(formMatch.Groups["body"].Value, "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string inputTag = inputMatch.Value;
            Dictionary<string, string> inputAttributes = ParseHtmlAttributes(inputTag);
            if (!inputAttributes.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            string type = inputAttributes.TryGetValue("type", out string inputType) ? inputType : string.Empty;
            if (type.Equals("submit", StringComparison.OrdinalIgnoreCase)
                || type.Equals("button", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(inputTag, "\\sdisabled(?:\\s|=|>|/)", RegexOptions.IgnoreCase))
            {
                continue;
            }
            SetQueryParameter(queryParameters, name, inputAttributes.TryGetValue("value", out string value) ? value : string.Empty);
        }
        resolvedUri = BuildUriWithQuery(actionUri, queryParameters);
        return true;
    }

    private static bool TryResolveMediaFireDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (pageUri == null || !IsHttpOrHttps(pageUri) || !IsExactHostOrSubdomain(pageUri.Host, "mediafire.com"))
        {
            return false;
        }
        foreach (Match anchorMatch in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(anchorMatch.Value);
            if (!attributes.TryGetValue("href", out string href) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }
            attributes.TryGetValue("id", out string id);
            attributes.TryGetValue("class", out string className);
            if (!string.Equals(id, "downloadButton", StringComparison.OrdinalIgnoreCase)
                && !(className?.IndexOf("popsok", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                continue;
            }
            if (Uri.TryCreate(pageUri, WebUtility.HtmlDecode(href), out Uri candidate)
                && IsHttpOrHttps(candidate)
                && IsExactHostOrSubdomain(candidate.Host, "mediafire.com"))
            {
                resolvedUri = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveManbowDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsManbowDownloadPageUri(pageUri))
        {
            return false;
        }
        Match match = Regex.Match(html, "(?:Down\\s*Load|Download)Address.*?<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }
        Dictionary<string, string> attributes = ParseHtmlAttributes(match.Value);
        return attributes.TryGetValue("href", out string href) && TryCreateResolvableDownloadUri(pageUri, href, allowDownloadSourcePageUri: true, out resolvedUri);
    }

    private static bool TryResolveBmsSearchDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (!IsVenueBmsSearchUri(pageUri) && !IsBmsSearchInfoUri(pageUri))
        {
            return false;
        }
        if (IsVenueBmsSearchUri(pageUri))
        {
            if (TryResolveSerializedVenueCoreDownloadUri(pageUri, html, out resolvedUri))
            {
                return true;
            }
            return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri: false, CollectSerializedVenueNonCoreDownloadKeys(pageUri, html), out resolvedUri);
        }
        if (TryResolveSerializedDownloadUri(pageUri, html, out resolvedUri))
        {
            return true;
        }
        return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri: false, out resolvedUri);
    }

    private static bool TryResolveAnchorDownloadUri(Uri pageUri, string html, bool allowDownloadSourcePageUri, out Uri resolvedUri)
    {
        return TryResolveAnchorDownloadUri(pageUri, html, allowDownloadSourcePageUri, null, out resolvedUri);
    }

    private static bool TryResolveAnchorDownloadUri(Uri pageUri, string html, bool allowDownloadSourcePageUri, HashSet<string> excludedDownloadKeys, out Uri resolvedUri)
    {
        resolvedUri = null;
        foreach (Match anchorMatch in Regex.Matches(html, "<a\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Dictionary<string, string> attributes = ParseHtmlAttributes(anchorMatch.Value);
            if (attributes.TryGetValue("href", out string href) && TryCreateResolvableDownloadUri(pageUri, href, allowDownloadSourcePageUri, out resolvedUri))
            {
                string downloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(downloadKey) && excludedDownloadKeys?.Contains(downloadKey) == true)
                {
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveSerializedVenueCoreDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        foreach (SerializedVenueDownloadCandidate candidate in EnumerateSerializedVenueDownloadCandidates(html))
        {
            if (!candidate.IsCore)
            {
                continue;
            }
            if (TryCreateResolvableDownloadUri(pageUri, candidate.Url, allowDownloadSourcePageUri: false, out resolvedUri))
            {
                return true;
            }
        }
        return false;
    }

    private static HashSet<string> CollectSerializedVenueNonCoreDownloadKeys(Uri pageUri, string html)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SerializedVenueDownloadCandidate candidate in EnumerateSerializedVenueDownloadCandidates(html))
        {
            if (candidate.IsCore)
            {
                continue;
            }
            if (TryCreateResolvableDownloadUri(pageUri, candidate.Url, allowDownloadSourcePageUri: false, out Uri resolvedUri))
            {
                string downloadKey = CreatePlaylistUrlDownloadKey(resolvedUri);
                if (!string.IsNullOrWhiteSpace(downloadKey))
                {
                    keys.Add(downloadKey);
                }
            }
        }
        return keys;
    }

    private sealed class SerializedVenueDownloadCandidate
    {
        internal SerializedVenueDownloadCandidate(string url, bool isCore)
        {
            Url = url;
            IsCore = isCore;
        }

        internal string Url { get; }

        internal bool IsCore { get; }
    }

    private static List<SerializedVenueDownloadCandidate> EnumerateSerializedVenueDownloadCandidates(string html)
    {
        var candidates = new List<SerializedVenueDownloadCandidate>();
        string text = DecodeEmbeddedJsonText(html);
        foreach (Match objectMatch in Regex.Matches(text, "\\{(?<body>[^{}]{0,2048}\"downloadURL\"[^{}]{0,2048})\\}", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string body = objectMatch.Groups["body"].Value;
            Match urlMatch = Regex.Match(body, "\"downloadURL\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase);
            if (!urlMatch.Success)
            {
                continue;
            }
            bool isCore = Regex.IsMatch(body, "\"type\"\\s*:\\s*\"CORE\"", RegexOptions.IgnoreCase);
            candidates.Add(new SerializedVenueDownloadCandidate(urlMatch.Groups["url"].Value, isCore));
        }
        return candidates;
    }

    private static bool TryResolveSerializedDownloadUri(Uri pageUri, string html, out Uri resolvedUri)
    {
        resolvedUri = null;
        string text = DecodeEmbeddedJsonText(html);
        foreach (Match match in Regex.Matches(text, "\"downloadURL\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase))
        {
            if (TryCreateResolvableDownloadUri(pageUri, match.Groups["url"].Value, allowDownloadSourcePageUri: false, out resolvedUri))
            {
                return true;
            }
        }
        foreach (Match blockMatch in Regex.Matches(text, "\"downloads\"\\s*:\\s*\\[(?<body>.*?)\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            foreach (Match urlMatch in Regex.Matches(blockMatch.Groups["body"].Value, "\"url\"\\s*:\\s*\"(?<url>[^\"<>]+)\"", RegexOptions.IgnoreCase))
            {
                if (TryCreateResolvableDownloadUri(pageUri, urlMatch.Groups["url"].Value, allowDownloadSourcePageUri: false, out resolvedUri))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsGoogleDriveHost(Uri uri)
    {
        if (uri == null)
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManbowDownloadPageUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("manbow.nothing.sh", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.IndexOf("event.cgi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsVenueBmsSearchUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("venue.bmssearch.net", StringComparison.OrdinalIgnoreCase)
            && IsVenueBmsSearchDetailPath(uri.AbsolutePath);
    }

    private static bool IsBmsSearchInfoUri(Uri uri)
    {
        return uri != null
            && IsHttpOrHttps(uri)
            && uri.Host.Equals("bmssearch.net", StringComparison.OrdinalIgnoreCase)
            && (uri.AbsolutePath.Equals("/bmses", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/bmses/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVenueBmsSearchDetailPath(string absolutePath)
    {
        string[] segments = (absolutePath ?? string.Empty).Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && int.TryParse(segments[segments.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryCreateResolvableDownloadUri(Uri pageUri, string href, bool allowDownloadSourcePageUri, out Uri resolvedUri)
    {
        resolvedUri = null;
        if (pageUri == null || string.IsNullOrWhiteSpace(href))
        {
            return false;
        }
        string decodedHref = WebUtility.HtmlDecode(href.Trim());
        if (!Uri.TryCreate(pageUri, decodedHref, out Uri candidate) || !IsHttpOrHttps(candidate))
        {
            return false;
        }
        Uri normalizedCandidate = NormalizeDownloadUri(candidate);
        if (!IsResolvableDownloadUri(normalizedCandidate, allowDownloadSourcePageUri))
        {
            return false;
        }
        resolvedUri = normalizedCandidate;
        return true;
    }

    private static bool IsResolvableDownloadUri(Uri uri, bool allowDownloadSourcePageUri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        if (IsDownloadAndInstallCandidateFileName(Path.GetFileName(uri.AbsolutePath)))
        {
            return true;
        }
        string host = uri.Host ?? string.Empty;
        if (host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(GetQueryParameter(uri, "id")))
        {
            return true;
        }
        if (IsKnownDownloadLandingPageUri(uri))
        {
            return true;
        }
        return allowDownloadSourcePageUri && IsDownloadSourcePageUri(uri);
    }

    private static bool IsKnownDownloadLandingPageUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        return host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || IsExactHostOrSubdomain(host, "mediafire.com");
    }

    private static bool IsMediaFireLandingPageUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || !IsHttpOrHttps(uri) || !IsExactHostOrSubdomain(uri.Host, "mediafire.com"))
        {
            return false;
        }
        string host = uri.Host ?? string.Empty;
        string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        return host.Equals("www.mediafire.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("mediafire.com", StringComparison.OrdinalIgnoreCase)
            || (segments.Length >= 1 && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDownloadSourcePageUri(Uri uri)
    {
        return IsManbowDownloadPageUri(uri)
            || IsVenueBmsSearchUri(uri)
            || IsBmsSearchInfoUri(uri);
    }

    private static string DecodeEmbeddedJsonText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string decoded = WebUtility.HtmlDecode(text);
        decoded = Regex.Replace(decoded, "\\\\u(?<hex>[0-9A-Fa-f]{4})", match =>
        {
            int value = int.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (value >= 0xD800 && value <= 0xDFFF)
            {
                return match.Value;
            }
            return char.ConvertFromUtf32(value);
        });
        return decoded.Replace("\\\"", "\"").Replace("\\/", "/");
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return attributes;
        }
        foreach (Match match in htmlAttributeRegex.Matches(tag))
        {
            string name = match.Groups["name"].Value;
            string value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                attributes[name] = WebUtility.HtmlDecode(value ?? string.Empty);
            }
        }
        return attributes;
    }

    private static async Task<string> ReadTextWithLimitAsync(Stream source, string charSet, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (source == null || maxBytes <= 0)
        {
            return null;
        }
        byte[] buffer = new byte[8192];
        using var memoryStream = new MemoryStream();
        int count;
        while ((count = await source.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)memoryStream.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await memoryStream.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            if (memoryStream.Length > maxBytes)
            {
                return null;
            }
        }
        try
        {
            Encoding encoding = string.IsNullOrWhiteSpace(charSet) ? Encoding.UTF8 : Encoding.GetEncoding(charSet.Trim('"'));
            return encoding.GetString(memoryStream.ToArray());
        }
        catch
        {
            return Encoding.UTF8.GetString(memoryStream.ToArray());
        }
    }

    private static string GetQueryParameter(Uri uri, string name)
    {
        if (uri == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        foreach (KeyValuePair<string, string> parameter in ParseQueryParameters(uri.Query))
        {
            if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }
        return null;
    }

    private static Uri SetUriQueryParameter(Uri uri, string name, string value)
    {
        var parameters = ParseQueryParameters(uri?.Query);
        SetQueryParameter(parameters, name, value);
        return BuildUriWithQuery(uri, parameters);
    }

    private static List<KeyValuePair<string, string>> ParseQueryParameters(string query)
    {
        var parameters = new List<KeyValuePair<string, string>>();
        string trimmedQuery = (query ?? string.Empty).TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return parameters;
        }
        foreach (string part in trimmedQuery.Split('&'))
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }
            int separatorIndex = part.IndexOf('=');
            string key = separatorIndex >= 0 ? part.Substring(0, separatorIndex) : part;
            string value = separatorIndex >= 0 ? part.Substring(separatorIndex + 1) : string.Empty;
            parameters.Add(new KeyValuePair<string, string>(Uri.UnescapeDataString(key.Replace("+", " ")), Uri.UnescapeDataString(value.Replace("+", " "))));
        }
        return parameters;
    }

    private static void SetQueryParameter(List<KeyValuePair<string, string>> parameters, string name, string value)
    {
        if (parameters == null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        parameters.RemoveAll(parameter => string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase));
        parameters.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
    }

    private static Uri BuildUriWithQuery(Uri uri, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        if (uri == null)
        {
            return null;
        }
        var builder = new UriBuilder(uri);
        builder.Query = string.Join("&", (parameters ?? []).Select(parameter => Uri.EscapeDataString(parameter.Key ?? string.Empty) + "=" + Uri.EscapeDataString(parameter.Value ?? string.Empty)));
        return builder.Uri;
    }

    private static bool IsSameUriWithoutFragment(Uri first, Uri second)
    {
        if (first == null || second == null)
        {
            return false;
        }
        var firstBuilder = new UriBuilder(first) { Fragment = string.Empty };
        var secondBuilder = new UriBuilder(second) { Fragment = string.Empty };
        return string.Equals(firstBuilder.Uri.ToString(), secondBuilder.Uri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddUriWithoutFragment(HashSet<string> uriSet, Uri uri)
    {
        if (uriSet == null || uri == null)
        {
            return false;
        }
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return uriSet.Add(builder.Uri.ToString());
    }

    private async Task<bool> TryCopyStreamToFileWithLimitAsync(Stream source, string destinationPath, long maxBytes, CancellationToken cancellationToken = default)
    {
        const int bufferSize = 81920;
        byte[] array = new byte[bufferSize];
        long totalBytes = 0L;
        bool completed = false;
        try
        {
            using FileStream fileStream = _downloadGateway.OpenWrite(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            int count;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = await source.ReadAsync(array, 0, array.Length, cancellationToken).ConfigureAwait(false);
                if (count <= 0)
                {
                    break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += count;
                if (totalBytes > maxBytes)
                {
                    return false;
                }
                await fileStream.WriteAsync(array, 0, count, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            return true;
        }
        finally
        {
            if (totalBytes > maxBytes || (!completed && cancellationToken.IsCancellationRequested))
            {
                try
                {
                    if (_downloadGateway.FileExists(destinationPath))
                    {
                        _downloadGateway.DeleteFile(destinationPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static string ResolveDownloadedArchiveFileName(Uri requestedUri, AppHttpResponse response)
    {
        if (TryResolveRawContentDispositionFileName(response?.ContentHeaders, out string rawFileName))
        {
            return rawFileName;
        }
        ContentDispositionHeaderValue contentDisposition = response?.ContentHeaders?.ContentDisposition;
        string fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return NormalizeDownloadedFileName(fileName);
        }
        if (response?.Headers?.Location != null)
        {
            return GetFileNameFromUri(response.Headers.Location);
        }
        if (Path.GetFileName(requestedUri.ToString()).Contains('?') || Path.GetFileName(requestedUri.ToString()).Contains('='))
        {
            return GetFileNameFromUri(response.ResponseUri);
        }
        return GetFileNameFromUri(requestedUri);
    }

    internal static string ResolveContentDispositionFileName(string contentDisposition)
    {
        return TryResolveContentDispositionFileName(contentDisposition, out string fileName) ? fileName : null;
    }

    private static bool TryResolveRawContentDispositionFileName(HttpContentHeaders headers, out string fileName)
    {
        fileName = null;
        if (headers == null || !headers.TryGetValues("Content-Disposition", out IEnumerable<string> contentDispositionValues))
        {
            return false;
        }
        foreach (string contentDisposition in contentDispositionValues)
        {
            if (TryResolveContentDispositionFileName(contentDisposition, out fileName))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryResolveContentDispositionFileName(string contentDisposition, out string fileName)
    {
        fileName = null;
        if (string.IsNullOrWhiteSpace(contentDisposition))
        {
            return false;
        }
        if (TryGetContentDispositionParameter(contentDisposition, "filename*", out string fileNameStar)
            && TryDecodeRfc5987Value(fileNameStar, out string decodedFileNameStar))
        {
            fileName = NormalizeDownloadedFileName(decodedFileNameStar);
            return !string.IsNullOrWhiteSpace(fileName);
        }
        if (TryGetContentDispositionParameter(contentDisposition, "filename", out string rawFileName))
        {
            fileName = NormalizeDownloadedFileName(RepairPossiblyMojibakeFileName(rawFileName));
            return !string.IsNullOrWhiteSpace(fileName);
        }
        return false;
    }

    private static bool TryGetContentDispositionParameter(string contentDisposition, string parameterName, out string value)
    {
        value = null;
        foreach (Match match in Regex.Matches(contentDisposition, "(?:^|;)\\s*(?<name>[^=;\\s]+)\\s*=\\s*(?:\"(?<quoted>(?:\\\\.|[^\"])*)\"|(?<bare>[^;]*))", RegexOptions.IgnoreCase))
        {
            if (!match.Groups["name"].Value.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value;
            value = value.Replace("\\\"", "\"").Trim();
            return true;
        }
        return false;
    }

    private static bool TryDecodeRfc5987Value(string value, out string decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        Match match = Regex.Match(value.Trim(), "^(?<charset>[^']*)'(?<language>[^']*)'(?<encoded>.*)$");
        if (!match.Success)
        {
            return false;
        }
        try
        {
            Encoding encoding = Encoding.GetEncoding(match.Groups["charset"].Value);
            decoded = DecodePercentEncodedBytes(match.Groups["encoded"].Value, encoding);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DecodePercentEncodedBytes(string value, Encoding encoding)
    {
        var bytes = new List<byte>();
        var builder = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && byte.TryParse(value.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedByte))
            {
                bytes.Add(parsedByte);
                i += 2;
                continue;
            }
            if (bytes.Count > 0)
            {
                builder.Append(encoding.GetString(bytes.ToArray()));
                bytes.Clear();
            }
            builder.Append(value[i]);
        }
        if (bytes.Count > 0)
        {
            builder.Append(encoding.GetString(bytes.ToArray()));
        }
        return builder.ToString();
    }

    private static string RepairPossiblyMojibakeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !LooksLikeLatin1Mojibake(fileName))
        {
            return fileName;
        }
        try
        {
            string repaired = Encoding.UTF8.GetString(Encoding.GetEncoding("ISO-8859-1").GetBytes(fileName));
            return ContainsJapaneseText(repaired) ? repaired : fileName;
        }
        catch
        {
            return fileName;
        }
    }

    private static bool LooksLikeLatin1Mojibake(string text)
    {
        return text.IndexOf('ã') >= 0
            || text.IndexOf('ä') >= 0
            || text.IndexOf('å') >= 0
            || text.IndexOf('æ') >= 0
            || text.IndexOf('ç') >= 0
            || text.IndexOf('è') >= 0
            || text.IndexOf('é') >= 0;
    }

    private static bool ContainsJapaneseText(string text)
    {
        return !string.IsNullOrEmpty(text) && text.Any(ch =>
            (ch >= '\u3040' && ch <= '\u30FF')
            || (ch >= '\u3400' && ch <= '\u9FFF'));
    }

    private static string NormalizeDownloadedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }
        string normalized = fileName.Trim().Trim('"');
        normalized = normalized.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = Path.GetFileName(normalized);
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '_');
        }
        return normalized;
    }

    private static string GetFileNameFromUri(Uri uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }
        return NormalizeDownloadedFileName(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)));
    }

}
