using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Scenarios.Tests.Fixtures;

/// <summary>シナリオの起動前設定と、標準起動が取得する固定 HTTP 入力を所有します。</summary>
internal sealed class ScenarioStartupFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task serve;
    private readonly IReadOnlyDictionary<string, string> responses;

    private ScenarioStartupFixture(string settingsPath, string libraryRoot, Action<Settings>? configure, IReadOnlyDictionary<string, string>? responses)
    {
        this.responses = responses ?? new Dictionary<string, string>();
        listener.Start();
        string baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        UpdateManifestUrl = baseUrl + "/update.json";
        BaseUrl = baseUrl;
        try
        {
            WriteSettings(settingsPath, libraryRoot, new Uri(baseUrl + "/table-list.json"), configure);
            serve = ServeAsync();
        }
        catch
        {
            listener.Stop();
            stop.Dispose();
            throw;
        }
    }

    /// <summary>標準更新確認へ渡す、固定 NoUpdate manifest の専有 loopback URL です。</summary>
    internal string UpdateManifestUrl { get; }

    /// <summary>宣言済み固定HTTP入力への絶対URLを作る専有基点です。</summary>
    internal string BaseUrl { get; }

    /// <summary>専有ファイルへ Standalone の起動前設定を保存し、必要な追加設定だけを呼出元が指定します。</summary>
    internal static ScenarioStartupFixture Create(string settingsPath, string libraryRoot, Action<Settings>? configure = null,
        IReadOnlyDictionary<string, string>? responses = null)
        => new(settingsPath, libraryRoot, configure, responses);

    /// <summary>実アプリ終了後に HTTP 待受と処理中接続を終了し、障害は呼出元へ返します。</summary>
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try { await serve.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Dispose(); }
    }

    private static void WriteSettings(string settingsPath, string libraryRoot, Uri tableListUrl, Action<Settings>? configure)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? throw new InvalidOperationException("設定の親がありません。"));
        var settings = new Settings();
        var provider = new PortableSettingsProvider(settingsPath);
        provider.Initialize(nameof(PortableSettingsProvider), new NameValueCollection());
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties) { property.Provider = provider; }
        settings.OperationModeLR2DB = false;
        settings.BMSRootPath = libraryRoot;
        settings.StandaloneBmsRootPaths = libraryRoot;
        settings.BMSInstallDir = libraryRoot;
        settings.Lang = "ja-JP";
        settings.AssemblyVersion = new SerializableVersion(typeof(ApplicationDatabaseScope).Assembly.GetName().Version
            ?? throw new InvalidOperationException("製品のバージョンがありません。"));
        settings.ScanBmsFilesOnStartup = true;
        settings.SkipInitPlaylistLoad = true;
        settings.EnablePlaylistUrlCompletion = false;
        settings.StartupSelectInstallPending = false;
        settings.TableListURL = tableListUrl;
        configure?.Invoke(settings);
        settings.Save();
    }

    private async Task ServeAsync()
    {
        const string updateManifest = """
            {"schemaVersion":1,"version":"0.0.0.0","releaseTag":"v0.0.0.0","releasePageUrl":"http://127.0.0.1/unused","packageFormatVersion":1,"minimumUpdaterVersion":"1","assets":[{"kind":"app","label":"SC01","fileName":"unused.zip","url":"http://127.0.0.1/unused.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","sizeBytes":1,"includesChartInfoMetadata":false}]}
            """;
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(stop.Token).ConfigureAwait(false);
            string? line;
            do { line = await reader.ReadLineAsync(stop.Token).ConfigureAwait(false); }
            while (line is { Length: > 0 });
            string[] request = requestLine?.Split(' ', 3) ?? [];
            string path = request.Length == 3 && request[0] == "GET" ? request[1].Split('?', 2)[0] : string.Empty;
            (string status, string body) = path switch
            {
                "/update.json" => ("200 OK", updateManifest),
                "/table-list.json" => ("200 OK", "[]"),
                _ => responses.TryGetValue(path, out string? declaredBody) ? ("200 OK", declaredBody) : ("404 Not Found", "{}")
            };
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] response = [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"), .. bodyBytes];
            await stream.WriteAsync(response, stop.Token).ConfigureAwait(false);
        }
    }
}
