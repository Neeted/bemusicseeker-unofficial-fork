using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistExternalPackageLookupServiceTests
{
    private const string SampleMd5 = "f8dcdfe070630bbb365323c662561a1a";

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

    [TestMethod]
    public void GingerProvider_RejectsMissingInvalidAndNonHttpDownloadUrl()
    {
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""fileName"":""package.7z""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""not a url""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""ftp://example.invalid/package.7z""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""https://www.mediafire.com/file/abc/package.7z/file""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, @"{""downloadURL"":""https://example.invalid/download""}", out _));
        Assert.IsFalse(GingerPlaylistExternalPackageLookupProvider.TryParseLookupResult(SampleMd5, "not-json", out _));
    }

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
    }

    [TestMethod]
    public async Task LookupService_StopsAtFirstProviderWhenDownloadSucceeds()
    {
        var first = new FakeProvider("A", new Uri("https://example.invalid/a.7z"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) => Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\a.7z", lookup.DownloadUri.AbsoluteUri)),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(0, second.LookupCount);
    }

    [TestMethod]
    public async Task LookupService_TriesNextProviderWhenDownloadFails()
    {
        var first = new FakeProvider("A", new Uri("https://example.invalid/a.7z"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.7z"));
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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        CollectionAssert.AreEqual(new[] { "A", "B" }, attemptedProviders);
    }

    [TestMethod]
    public async Task LookupService_TreatsProviderTimeoutAsFailureAndTriesNextProvider()
    {
        var first = new ThrowingProvider("A", new TaskCanceledException("timeout"));
        var second = new FakeProvider("B", new Uri("https://example.invalid/b.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) => Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\b.7z", lookup.DownloadUri.AbsoluteUri)),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Downloaded, result.Kind);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(1, second.LookupCount);
    }

    [TestMethod]
    public async Task LookupService_TreatsDownloadTimeoutAsFailureWhenUserDidNotCancel()
    {
        var first = new FakeProvider("A", new Uri("https://example.invalid/a.7z"));
        var service = new PlaylistExternalPackageLookupService([first]);

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) => throw new TaskCanceledException("timeout"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, result.Kind);
    }

    [TestMethod]
    public async Task LookupService_SkipsAlreadyDownloadedUrlWithoutTryingLowerPriorityProvider()
    {
        Uri packageUri = new("https://example.invalid/package.7z");
        var first = new FakeProvider("A", packageUri);
        var second = new FakeProvider("B", new Uri("https://example.invalid/other.7z"));
        var service = new PlaylistExternalPackageLookupService([first, second]);
        var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packageUri.AbsoluteUri };
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            },
            downloadedKeys,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl, result.Kind);
        Assert.AreEqual(0, downloadAttemptCount);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(0, second.LookupCount);
    }

    [TestMethod]
    public async Task LookupService_DoesNotRetryFailedUrlInSameRun()
    {
        Uri failedUri = new("https://example.invalid/package.7z");
        var first = new FakeProvider("A", failedUri);
        var service = new PlaylistExternalPackageLookupService([first]);
        var failedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult firstResult = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Failed());
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            failedKeys,
            uri => uri.AbsoluteUri,
            _ => { });
        PlaylistExternalPackageWorkflowResult secondResult = await service.DownloadFirstAvailablePackageAsync(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Downloaded("C:\\temp\\package.7z", lookup.DownloadUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            failedKeys,
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, firstResult.Kind);
        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.DuplicateFailedUrl, secondResult.Kind);
        Assert.AreEqual(1, downloadAttemptCount);
        Assert.IsTrue(failedKeys.Contains(failedUri.AbsoluteUri));
    }

    [TestMethod]
    public async Task LookupService_DoesNotRetryRedirectedFailedUrlInSameRun()
    {
        Uri sourceUri = new("https://example.invalid/download?id=package");
        Uri finalUri = new("https://cdn.example.invalid/package.7z");
        var first = new FakeProvider("A", sourceUri);
        var second = new FakeProvider("B", finalUri);
        var service = new PlaylistExternalPackageLookupService([first, second]);
        var failedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int downloadAttemptCount = 0;

        PlaylistExternalPackageWorkflowResult result = await service.DownloadFirstAvailablePackageAsync(
            SampleMd5,
            (lookup, token) =>
            {
                downloadAttemptCount++;
                return Task.FromResult(PlaylistExternalPackageDownloadAttempt.Failed(finalUri.AbsoluteUri));
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            failedKeys,
            uri => uri.AbsoluteUri,
            _ => { });

        Assert.AreEqual(PlaylistExternalPackageWorkflowResultKind.Failed, result.Kind);
        Assert.AreEqual(1, downloadAttemptCount);
        Assert.AreEqual(1, first.LookupCount);
        Assert.AreEqual(1, second.LookupCount);
        Assert.IsTrue(failedKeys.Contains(sourceUri.AbsoluteUri));
        Assert.IsTrue(failedKeys.Contains(finalUri.AbsoluteUri));
    }

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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            uri => uri.AbsoluteUri,
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
