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
    /// <summary>見出しURL不明の専用例外はHeaderNotFoundへ分類し、通常通信失敗として案内する誤りを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesHeaderUrlNotFound()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/table.html"), new PlaylistHeaderUriNotFoundException(new Uri("https://example.com/table.html")));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderNotFound, result.FailureKind);
    }

    /// <summary>従来のheader引数例外を見出し解析失敗へ分類し、既存例外形式の互換性欠落を検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesHeaderParse()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new ArgumentException("header", "_header_json"));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, result.FailureKind);
    }

    /// <summary>見出し解析の専用例外を正しく分類し内部FormatExceptionの原因詳細を保持して、解析失敗の原因消失を検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesHeaderParseException()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new PlaylistHeaderParseException("header", new FormatException("bad json")));

        Assert.AreEqual(PlaylistSyncStatusKind.HeaderParseError, result.FailureKind);
        StringAssert.Contains(result.Detail, "bad json");
    }

    /// <summary>従来のdata引数例外はdata解析失敗へ分類し、header失敗との取り違えを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesDataParse()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/score.json"), new ArgumentException("data", "_data_json"));

        Assert.AreEqual(PlaylistSyncStatusKind.DataParseError, result.FailureKind);
    }

    /// <summary>data解析の専用例外を分類し内部原因を詳細へ残して、入力失敗を空data成功へ隠す誤りを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesDataParseException()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/score.json"), new PlaylistDataParseException("data", new FormatException("bad data")));

        Assert.AreEqual(PlaylistSyncStatusKind.DataParseError, result.FailureKind);
        StringAssert.Contains(result.Detail, "bad data");
    }

    /// <summary>相対data_url解決失敗の従来診断をInvalidDataUrlへ分類し、通信や解析失敗との取り違えを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesInvalidDataUrl()
    {
        var result = PlaylistSyncAttemptResult.CreateFailure(null, new Uri("https://example.com/header.json"), new InvalidOperationException("Failed to resolve playlist data_url. rawDataUrl=./score.json"));

        Assert.AreEqual(PlaylistSyncStatusKind.InvalidDataUrl, result.FailureKind);
    }

    /// <summary>404・403は固有分類、500は一般HTTP分類へ分けて、利用者へ異なる通信原因を案内する誤りを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesHttpStatusCodes()
    {
        Assert.AreEqual(PlaylistSyncStatusKind.Http404, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=404 status=NotFound")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.Http403, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=403 status=Forbidden")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.HttpError, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("HTTP request failed statusCode=500 status=InternalServerError")).FailureKind);
    }

    /// <summary>timeout取消とsocketを内部原因に持つHTTP失敗をNetworkErrorへ分類し、解析やHTTP status失敗として案内する誤りを検出します。</summary>
    [TestMethod]
    public void CreateFailure_ClassifiesNetworkFailures()
    {
        Assert.AreEqual(PlaylistSyncStatusKind.NetworkError, PlaylistSyncAttemptResult.CreateFailure(null, null, new TaskCanceledException("timeout")).FailureKind);
        Assert.AreEqual(PlaylistSyncStatusKind.NetworkError, PlaylistSyncAttemptResult.CreateFailure(null, null, new HttpRequestException("network", new SocketException())).FailureKind);
    }


}
