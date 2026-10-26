using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>外部照会応答の解析と候補取得の順序・重複・取消を検証する。</summary>
/// <remarks>解析規則と取得制御へ入力を直接渡す。実HTTPと導入後の通知はURL取得所有者のテストへ分担する。</remarks>
[TestClass]
public sealed class PlaylistExternalPackageLookupServiceTests
{
    private const string SampleMd5 = "f8dcdfe070630bbb365323c662561a1a";

    /// <summary>Ginger応答のHTTPアーカイブURLとファイル情報を抽出し、大文字MD5を正規化する。応答フィールドの誤対応と照会対象の取り違えを検出する。</summary>
    [TestMethod]
    public void GingerProvider_ParsesValidDownloadUrl()
    {
        string json = @"{
  ""shardMD5"": ""8f074cec5e1fd331c02f36c1cd3a158c"",
  ""fileName"": ""星の器～STAR OF ANDROMEDA.7z"",
  ""fileSize"": 18927067,
  ""downloadURL"": ""https://pixeldrain.com/api/filesystem/SicwJL1j/package.7z""
}";

        bool parsed = GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5.ToUpperInvariant(), json, out PlaylistExternalPackageLookupResult result);

        Assert.IsTrue(parsed);
        Assert.AreEqual(GingerPlaylistExternalPackageLookupProvider.ProviderIdValue, result.ProviderId);
        Assert.AreEqual(SampleMd5, result.ChartMd5);
        Assert.AreEqual("https://pixeldrain.com/api/filesystem/SicwJL1j/package.7z", result.DownloadUri.ToString());
        Assert.AreEqual("星の器～STAR OF ANDROMEDA.7z", result.FileName);
        Assert.AreEqual(18927067L, result.FileSize);
    }

    /// <summary>URL欠損・不正形式・FTP・案内ページ・拡張子なし・不正JSONを取得候補にしない。取得可能なアーカイブと案内URLを混同する解析を検出する。</summary>
    [TestMethod]
    public void GingerProvider_RejectsMissingInvalidAndNonHttpDownloadUrl()
    {
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""fileName"":""package.7z""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""not a url""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""ftp://example.invalid/package.7z""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""https://www.mediafire.com/file/abc/package.7z/file""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""https://example.invalid/download""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, "not-json", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"42", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":{}}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""https://example.invalid/package.zip"",""fileSize"":{}}", out _));
    }

    /// <summary>Konmai成功応答のネストしたsong_urlと曲名を取得し、大文字MD5を正規化する。別階層の解析や対象ハッシュの誤配送を検出する。</summary>
    [TestMethod]
    public void KonmaiProvider_ParsesSuccessSongUrl()
    {
        string json = @"{
  ""result"": ""success"",
  ""msg"": """",
  ""data"": {
    ""song_name"": ""星の器～STAR OF ANDROMEDA"",
    ""song_url"": ""https://bms.alvorna.com/bms/zipped/package.7z""
  }
}";

        bool parsed = KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5.ToUpperInvariant(), json, out PlaylistExternalPackageLookupResult result);

        Assert.IsTrue(parsed);
        Assert.AreEqual(KonmaiPlaylistExternalPackageLookupProvider.ProviderIdValue, result.ProviderId);
        Assert.AreEqual(SampleMd5, result.ChartMd5);
        Assert.AreEqual("https://bms.alvorna.com/bms/zipped/package.7z", result.DownloadUri.ToString());
        Assert.AreEqual("星の器～STAR OF ANDROMEDA", result.FileName);
    }

    /// <summary>失敗応答・URL欠損・不正形式・ローカルファイル・案内ページ・拡張子なし・不正JSONを候補にしない。成功判定の無視や非アーカイブ取得を検出する。</summary>
    [TestMethod]
    public void KonmaiProvider_RejectsNonSuccessMissingInvalidAndNonHttpSongUrl()
    {
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""error"",""data"":{""song_url"":""https://example.invalid/package.7z""}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{""song_url"":""not a url""}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{""song_url"":""file:///C:/package.7z""}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{""song_url"":""https://www.mediafire.com/file/abc/package.7z/file""}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{""song_url"":""https://example.invalid/download""}}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, "not-json", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"42", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":42}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":[]}", out _));
        Assert.IsFalse(KonmaiPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""result"":""success"",""data"":{""song_url"":{}}}", out _));
    }

    /// <summary>先頭候補の取得成功は返却直前に取消されてもパスとキーを保持し、後続提供元を照会しない。完了済み取得物の喪失と成功後の重複取得を検出する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LookupService_StopsAtFirstProviderWhenDownloadSucceeds(bool cancelAfterCompletedDownload)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new FakeProvider("A", new Uri("https://example.invalid/a.7z"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);
        const string savedPath = "C:\\temp\\a.7z";
        const string savedKey = "https://cdn.example.invalid/a.7z";
        var downloadedKeys = new HashSet<string>(StringComparer.Ordinal);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                var completed = PlaylistExternalPackageDownloadAttempt.Downloaded(savedPath, savedKey);
                if (cancelAfterCompletedDownload) { cancellation.Cancel(); }
                return Task.FromResult(completed);
            },
            downloadedKeys,
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { },
            cancellation.Token);

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        Assert.AreEqual(savedPath, result.FilePath);
        Assert.AreEqual(savedKey, result.DownloadKey);
        Assert.AreEqual(1, downloadedKeys.Count);
        Assert.IsTrue(downloadedKeys.Contains(savedKey));
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(0, second.LookupCount);
    }

    /// <summary>先頭候補の取得失敗後にpath caseだけ違う次提供元を順に試して成功を返します。失敗の早期打切りと別実体の誤除外を検出します。</summary>
    [TestMethod]
    public async Task LookupService_TriesNextProviderWhenDownloadFails()
    {
        var first = new FakeProvider("A", new Uri("https://example.invalid/Package.7z"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/package.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);
        var attemptedProviders = new List<string>();

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                attemptedProviders.Add(lookup.ProviderId);
                return Task.FromResult(lookup.ProviderId == "A"
                    ? PlaylistExternalPackageDownloadAttempt.Failed()
                    : PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\b.7z", lookup.DownloadUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        CollectionAssert.AreEqual(new[] { "A", "B" }, attemptedProviders);
    }

    /// <summary>利用者取消なしの照会タイムアウトは、診断故障の有無にかかわらず次提供元へ進む。通信タイムアウトや診断故障を全体取消・操作失敗に置換する誤りを検出する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LookupService_TreatsProviderTimeoutAsFailureAndTriesNextProvider(bool failDiagnostic)
    {
        var first = new ThrowingProvider("A", new TaskCanceledException("timeout"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) => Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\b.7z", lookup.DownloadUri.AbsoluteUri)),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { if (failDiagnostic) { throw new IOException("diagnostic failed"); } });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(1, second.LookupCount);
    }

    /// <summary>利用者取消なしの取得タイムアウトは失敗結果を返す。通信タイムアウトと利用者取消の混同を検出する。</summary>
    [TestMethod]
    public async Task LookupService_TreatsDownloadTimeoutAsFailureWhenUserDidNotCancel()
    {
        var first = new FakeProvider("A", new Uri("https://example.invalid/a.7z"));
        var service = new PlaylistExternalPackageLookupService([first]);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) => throw new TaskCanceledException("timeout"),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, result.Kind);
    }

    /// <summary>内部故障・資源不足・回収不能を次候補への継続にせず、失敗済みURLとしても記録しません。</summary>
    [DataTestMethod]
    [DataRow("lookup", "internal")]
    [DataRow("lookup", "resource")]
    [DataRow("download", "internal")]
    [DataRow("download", "resource")]
    [DataRow("download", "cleanup")]
    public async Task LookupService_PropagatesInternalAndUnresolvedCleanupFailures(string stage, string kind)
    {
        Exception failure = kind switch
        {
            "resource" => new OutOfMemoryException("resource failure"),
            "cleanup" => new AggregateException(new IOException("staging remains")),
            _ => new InvalidOperationException("internal failure")
        };
        IPlaylistExternalPackageLookupProvider first = stage == "lookup"
            ? new ThrowingProvider("A", failure)
            : new FakeProvider("A", new Uri("https://example.invalid/a.zip"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.zip"));
        var failedKeys = new HashSet<string>();
        var service = new PlaylistExternalPackageLookupService([first, second]);
        Exception? observed = null;
        try
        {
            await service.DownloadFirstAvailablePackageAsync(
                SampleMd5, (_, _) => throw failure, new HashSet<string>(), failedKeys,
                PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey, _ => { });
        }
        catch (Exception exception) { observed = exception; }
        Assert.AreSame(failure, observed);
        Assert.AreEqual(0, second.LookupCount);
        Assert.AreEqual(0, failedKeys.Count);
    }

    /// <summary>既に取得済みの先頭URLでは取得も下位提供元の照会も行わず重複結果を返す。同一作業内の重複取得を検出する。</summary>
    [TestMethod]
    public async Task LookupService_SkipsAlreadyDownloadedUrlWithoutTryingLowerPriorityProvider()
    {
        Uri packageUri = new("https://example.invalid/package.7z");
        var first = new FakeProvider("A", packageUri);
        var second = new FakeProvider("B", new Uri("https://example.invalid/other.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);
        var downloadedKeys = new HashSet<string>(StringComparer.Ordinal) { packageUri.AbsoluteUri };
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            },
            downloadedKeys,
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl, result.Kind);
        Assert.AreEqual(0, downloadAttemptCount);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(0, second.LookupCount);
    }

    /// <summary>別MD5から同じ失敗実体のhost/fragment別表記を得ても再取得せず、case別候補は成功させます。失敗状態のMD5限定とURL全体の大小文字無視を検出します。</summary>
    [TestMethod]
    public async Task LookupService_DoesNotRetryFailedUrlInSameRun()
    {
        Uri failedUri = new("https://example.invalid/Package.7z");
        var first = new FakeProvider("A", failedUri);
        var service = new PlaylistExternalPackageLookupService([first]);
        var failedKeys = new HashSet<string>(StringComparer.Ordinal);
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult firstResult = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Failed());
            },
            new HashSet<string>(StringComparer.Ordinal),
            failedKeys,
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });
        var sameService = new PlaylistExternalPackageLookupService([new FakeProvider("A", new Uri("HTTPS://EXAMPLE.INVALID/Package.7z#again"))]);
        PlaylistExternalPackageWorkflowResult secondResult = await sameService.DownloadFirstAvailablePackageAsync(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.Ordinal),
            failedKeys,
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });

        Uri distinctUri = new("https://example.invalid/package.7z");
        var distinctService = new PlaylistExternalPackageLookupService([new FakeProvider("A", distinctUri)]);
        PlaylistExternalPackageWorkflowResult thirdResult = await distinctService.DownloadFirstAvailablePackageAsync(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            }, new HashSet<string>(StringComparer.Ordinal), failedKeys,
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey, _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, firstResult.Kind);
        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.DuplicateFailedUrl, secondResult.Kind);
        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, thirdResult.Kind);
        Assert.AreEqual(2, downloadAttemptCount);
        Assert.IsTrue(failedKeys.Contains(failedUri.AbsoluteUri));
    }

    /// <summary>取得元とリダイレクト先の両方を失敗URLとして記録し、後続提供元が最終URLを返しても再取得しない。転送先による重複判定の抜けを検出する。</summary>
    [TestMethod]
    public async Task LookupService_DoesNotRetryRedirectedFailedUrlInSameRun()
    {
        Uri sourceUri = new("https://example.invalid/download.zip?id=package");
        Uri finalUri = new("https://cdn.example.invalid/package.7z");
        var first = new FakeProvider("A", sourceUri);
        var second = new FakeProvider("B", finalUri);
        var service = new PlaylistExternalPackageLookupService([first, second]);
        var failedKeys = new HashSet<string>(StringComparer.Ordinal);
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Failed(finalUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.Ordinal),
            failedKeys,
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, result.Kind);
        Assert.AreEqual(1, downloadAttemptCount);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(1, second.LookupCount);
        Assert.IsTrue(failedKeys.Contains(sourceUri.AbsoluteUri));
        Assert.IsTrue(failedKeys.Contains(finalUri.AbsoluteUri));
    }

    /// <summary>照会完了時に利用者取消が成立したら取得へ進まず取消例外を返す。照会後の取消確認漏れを検出する。</summary>
    [TestMethod]
    public async Task LookupService_DoesNotStartDownloadWhenCanceledAfterLookup()
    {
        using var cancellation = new CancellationTokenSource();
        Uri packageUri = new("https://example.invalid/package.7z");
        var service = new PlaylistExternalPackageLookupService([new CancelingProvider("A", packageUri, cancellation)]);
        bool downloadAttempted = false;

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttempted = true;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
            _ => { },
            cancellation.Token));

        Assert.IsFalse(downloadAttempted);
    }

    private sealed class FakeProvider : IPlaylistExternalPackageLookupProvider
    {
        private readonly Uri downloadUri;

        internal FakeProvider(string providerId, Uri downloadUri)
        {
            ProviderId = providerId;
            this.downloadUri = downloadUri;
        }

        public string ProviderId { get; }

        internal int LookupCount { get; private set; }

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUri));
        }
    }

    private sealed class ThrowingProvider : IPlaylistExternalPackageLookupProvider
    {
        private readonly Exception exception;

        internal ThrowingProvider(string providerId, Exception exception)
        {
            ProviderId = providerId;
            this.exception = exception;
        }

        public string ProviderId { get; }

        internal int LookupCount { get; private set; }

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            LookupCount++;
            throw exception;
        }
    }

    private sealed class CancelingProvider : IPlaylistExternalPackageLookupProvider
    {
        private readonly Uri downloadUri;
        private readonly CancellationTokenSource cancellation;

        internal CancelingProvider(string providerId, Uri downloadUri, CancellationTokenSource cancellation)
        {
            ProviderId = providerId;
            this.downloadUri = downloadUri;
            this.cancellation = cancellation;
        }

        public string ProviderId { get; }

        public Task<PlaylistExternalPackageLookupResult> LookupAsync(string chartMd5, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new PlaylistExternalPackageLookupResult(ProviderId, chartMd5, downloadUri));
        }
    }
}
