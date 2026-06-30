using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSyncAttemptResultTests
{
    [TestMethod]
    public void CreateFailure_ClassifiesHeaderUrlNotFound()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/table.html"), new PlaylistHeaderUriNotFoundException(new Uri("https://example.com/table.html")));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderNotFound, result.FailureKind);
    }

    [TestMethod]
    public void CreateFailure_ClassifiesHeaderParse()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new ArgumentException("header", "_header_json"));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, result.FailureKind);
    }

    [TestMethod]
    public void CreateFailure_ClassifiesHeaderParseException()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new PlaylistHeaderParseException("header", new FormatException("bad json")));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, result.FailureKind);
        StringAssert.Contains(result.Detail, "bad json");
    }

    [TestMethod]
    public void CreateFailure_ClassifiesDataParse()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/score.json"), new ArgumentException("data", "_data_json"));

        Assert.AreEqual(PlaylistSyncStatusKind.DataParseError, result.FailureKind);
    }

    [TestMethod]
    public void CreateFailure_ClassifiesDataParseException()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/score.json"), new PlaylistDataParseException("data", new FormatException("bad data")));

        Assert.AreEqual(PlaylistSyncStatusKind.DataParseError, result.FailureKind);
        StringAssert.Contains(result.Detail, "bad data");
    }

    [TestMethod]
    public void CreateFailure_ClassifiesInvalidDataUrl()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new InvalidOperationException("Failed to resolve playlist data_url. rawDataUrl=./score.json"));

        Assert.AreEqual(PlaylistSyncStatusKind.InvalidDataUrl, result.FailureKind);
    }

    [TestMethod]
    public void CreateFailure_ClassifiesHttpStatusCodes()
    {
        Assert.AreEqual(PlaylistSyncStatusKind.Http404, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=404 status=NotFound")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.Http403, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=403 status=Forbidden")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.HttpError, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=500 status=InternalServerError")).FailureKind);
    }

    [TestMethod]
    public void CreateFailure_ClassifiesNetworkFailures()
    {
        Assert.AreEqual(PlaylistSyncStatusKind.NetworkError, PlaylistSyncAttemptResult.CreateFailure(null, null, new TaskCanceledException("timeout")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.NetworkError, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("network", new SocketException())).FailureKind);
    }

    [TestMethod]
    public void CreateFailure_WithResultTable_AssociatesFailureWithRegisteredTable()
    {
        var sourceTable = new BMSTable { name = "source" };
        var resultTable = new BMSTable { name = "restored" };
        var exception = new HttpRequestException("HTTP request failed statusCode=404 status=NotFound");

        PlaylistSyncAttemptResult result = PlaylistSyncAttemptResult.CreateFailure(
            sourceTable,
            resultTable,
            new Uri("https://example.com/table.html"),
            exception);

        Assert.AreSame(sourceTable, result.SourceTable);
        Assert.AreSame(resultTable, result.ResultTable);
        Assert.AreEqual(PlaylistSyncStatusKind.Http404, result.FailureKind);
        Assert.IsFalse(result.Succeeded);
    }
}
