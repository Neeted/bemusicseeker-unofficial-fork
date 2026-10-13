using System;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

/// <summary>実bmson入力による形式失敗を、内部故障と混同せず譜面単位に留めます。</summary>
[TestClass]
public sealed class BmsonInputFailureBoundaryTests
{
    /// <summary>
    /// オブジェクト欄の異なる型・nullイベント・nullノート・表現不能な長さを実解析へ渡し、例外で操作を中断せず解析失敗の結果を返すことを確認します。
    /// 配列や任意チャンネル自体のnullは別ケースで許容を確認し、内部故障の伝播は既存のsnapshot評価テストが分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow("42")]
    [DataRow("{\"info\":42}")]
    [DataRow("{\"info\":\"invalid\"}")]
    [DataRow("{\"sound_channels\":[1]}")]
    [DataRow("{\"sound_channels\":[{\"notes\":[1]}]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"bpm_events\":[null]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"BPM_EVENTS\":[null]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"stop_events\":[null]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"scroll_events\":[null]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"lines\":[null]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"sound_channels\":[{\"notes\":[null]}]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"key_channels\":[{\"notes\":[null]}]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"mine_channels\":[{\"notes\":[null]}]}")]
    [DataRow("{\"info\":{\"init_bpm\":120},\"bga\":{\"bga_events\":[null]}}")]
    [DataRow("{\"info\":{\"init_bpm\":1,\"resolution\":1},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":50000}]}]}")]
    public void EvaluateSnapshot_MalformedBmsonRemainsTargetFailure(string json)
    {
        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = Evaluate(json);

        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.TimeoutFailed);
        Assert.IsNull(result.Row);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual("parse_failed", result.ParseFailureRow.failure_kind);
    }

    /// <summary>省略可能な配列・チャンネルのnullを全面拒否せず、有効ノートの解析結果を保持します。</summary>
    [TestMethod]
    public void EvaluateSnapshot_OptionalNullArraysAndChannelsRemainAccepted()
    {
        const string json = "{\"info\":{\"init_bpm\":120},\"lines\":null,\"bpm_events\":null,"
            + "\"stop_events\":null,\"scroll_events\":null,\"key_channels\":[null],\"mine_channels\":[null],"
            + "\"sound_channels\":[null,{\"notes\":[{\"x\":1,\"y\":240}]}]}";
        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = Evaluate(json);

        Assert.IsFalse(result.ParseFailed);
        Assert.IsNotNull(result.Row);
        Assert.IsTrue(result.ShouldPersistRow);
        Assert.AreEqual(1, result.Row.notes);
        Assert.IsNull(result.ParseFailureRow);
    }

    /// <summary>最大int位置のイベントを、他種イベントの枯渇を示す番兵と混同して存在しない配列を参照しません。</summary>
    [DataTestMethod]
    [DataRow("bpm_events", "bpm", "1000000000")]
    [DataRow("stop_events", "duration", "0")]
    [DataRow("scroll_events", "rate", "1")]
    public void EvaluateSnapshot_MaximumEventPositionDoesNotSelectExhaustedArray(string events, string property, string value)
    {
        // 時間の範囲超過とは分離し、位置だけをint上限に置きます。
        string json = "{\"info\":{\"init_bpm\":1000000000,\"resolution\":240},\"" + events
            + "\":[{\"y\":2147483647,\"" + property + "\":" + value + "}]}";
        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = Evaluate(json);

        Assert.IsFalse(result.ParseFailed);
        Assert.IsNotNull(result.Row);
        Assert.IsNull(result.ParseFailureRow);
    }

    /// <summary>JSON例外に包まれた内部故障を入力不正と誤認せず、詳細解析でも元の原因を返して永続失敗結果を作りません。</summary>
    [DataTestMethod]
    [DataRow("internal")]
    [DataRow("resource")]
    [DataRow("argument")]
    [DataRow("null")]
    [DataRow("range")]
    public void EvaluateSnapshot_WrappedInternalFailureIsNotInputFailure(string failureKind)
    {
        Exception cause = failureKind switch
        {
            "resource" => new OutOfMemoryException("parser resource failure"),
            "argument" => new ArgumentException("parser argument failure", "contract"),
            "null" => new ArgumentNullException("contract"),
            "range" => new ArgumentOutOfRangeException("contract"),
            _ => new InvalidOperationException("parser internal failure")
        };
        var failure = new JsonSerializationException("parser callback failed", cause);
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        ChartFile file = ChartTestValues.Empty() with { Path = "input.bmson", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, Encoding.UTF8.GetBytes("{}"), DateTime.UtcNow, md5, sha256);
        var service = new ChartInfoBuildService(null, parseBytes: (_, _, _, _, _) => throw failure);
        ChartInfoBuildService.ChartInfoSnapshotBuildResult? result = null;

        JsonSerializationException observed = Assert.ThrowsException<JsonSerializationException>(() =>
            result = service.EvaluateSnapshot(snapshot, ChartInfoBuildTargetMapper.Create(file), null, false));

        Assert.AreSame(failure, observed);
        Assert.AreSame(cause, observed.InnerException);
        Assert.IsNull(result);
    }

    private static ChartInfoBuildService.ChartInfoSnapshotBuildResult Evaluate(string json)
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        ChartFile file = ChartTestValues.Empty() with { Path = "input.bmson", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, Encoding.UTF8.GetBytes(json), DateTime.UtcNow, md5, sha256);
        return new ChartInfoBuildService().EvaluateSnapshot(
            snapshot, ChartInfoBuildTargetMapper.Create(file), null, hasCurrentParseFailure: false);
    }
}
