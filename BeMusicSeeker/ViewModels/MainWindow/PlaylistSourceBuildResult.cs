using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// playlist source build stage の集計メトリクスです。
/// </summary>
internal sealed class PlaylistScoreProbeMetrics
{
    internal int TargetCount;

    internal int MatchedScoreCount;

    internal long TotalMs;
}

/// <summary>
/// playlist source build stage の結果です。
/// </summary>
internal sealed class PlaylistSourceBuildResult
{
    internal PlaylistSourceBuildResult(
        List<PlaylistDetailSourceRow> sourceRows,
        int scoreUpdateTargetCount,
        long entryResolveMs,
        long scoreProbeMs,
        long sourceMaterializeMs,
        PlaylistScoreProbeMetrics scoreProbeMetrics)
    {
        SourceRows = sourceRows ?? [];
        SourceCount = SourceRows.Count;
        FolderCount = SourceCount;
        ScoreUpdateTargetCount = scoreUpdateTargetCount;
        EntryResolveMs = entryResolveMs;
        ScoreProbeMs = scoreProbeMs;
        SourceMaterializeMs = sourceMaterializeMs;
        ScoreProbeMetrics = scoreProbeMetrics ?? new PlaylistScoreProbeMetrics();
    }

    internal List<PlaylistDetailSourceRow> SourceRows { get; }

    internal int SourceCount { get; }

    internal int FolderCount { get; }

    internal int ScoreUpdateTargetCount { get; }

    internal long EntryResolveMs { get; }

    internal long ScoreProbeMs { get; }

    internal long SourceMaterializeMs { get; }

    internal PlaylistScoreProbeMetrics ScoreProbeMetrics { get; }
}

/// <summary>
/// playlist source から view rows を作る stage の結果です。
/// </summary>
internal sealed class PlaylistViewApplyResult
{
    internal PlaylistViewApplyResult(
        IList finalRows,
        int sourceCount,
        string sortProfile,
        int keywordCount,
        int modeCount,
        long keywordStageMs,
        long modeStageMs,
        long sortStageMs,
        long viewMaterializeMs)
    {
        FinalRows = finalRows ?? throw new System.ArgumentNullException(nameof(finalRows));
        SourceCount = sourceCount;
        ViewCount = FinalRows.Count;
        SortProfile = sortProfile;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        KeywordStageMs = keywordStageMs;
        ModeStageMs = modeStageMs;
        SortStageMs = sortStageMs;
        ViewMaterializeMs = viewMaterializeMs;
    }

    internal IList FinalRows { get; }

    internal int SourceCount { get; }

    internal int ViewCount { get; }

    internal string SortProfile { get; }

    internal int KeywordCount { get; }

    internal int ModeCount { get; }

    internal long KeywordStageMs { get; }

    internal long ModeStageMs { get; }

    internal long SortStageMs { get; }

    internal long ViewMaterializeMs { get; }
}

/// <summary>
/// playlist detail view rows を main view へ反映した結果です。
/// </summary>
internal sealed class PlaylistMainViewApplyResult
{
    internal PlaylistMainViewApplyResult(long columnStageMs, long callbackStageMs)
    {
        ColumnStageMs = columnStageMs;
        CallbackStageMs = callbackStageMs;
    }

    internal long ColumnStageMs { get; }

    internal long CallbackStageMs { get; }
}

internal sealed class PlaylistDetailTerminalApplyResult
{
    internal PlaylistDetailTerminalApplyResult(
        bool applied,
        PlaylistViewApplyResult viewApply,
        PlaylistMainViewApplyResult mainViewApply,
        int previousSourceCount)
    {
        Applied = applied;
        ViewApply = viewApply;
        MainViewApply = mainViewApply;
        PreviousSourceCount = previousSourceCount;
    }

    internal bool Applied { get; }

    internal PlaylistViewApplyResult ViewApply { get; }

    internal PlaylistMainViewApplyResult MainViewApply { get; }

    internal int PreviousSourceCount { get; }
}

[Serializable]
internal sealed class PlaylistDetailTerminalPublishException : Exception
{
    internal PlaylistDetailTerminalPublishException()
    {
    }

    internal PlaylistDetailTerminalPublishException(string message)
        : base(message)
    {
    }

    internal PlaylistDetailTerminalPublishException(Exception innerException)
        : this(innerException, ownershipTransferred: true)
    {
    }

    internal PlaylistDetailTerminalPublishException(Exception innerException, bool ownershipTransferred)
        : base("Playlist detail terminal state was committed but disposal or publishing failed.", innerException)
    {
        OwnershipTransferred = ownershipTransferred;
    }

    internal PlaylistDetailTerminalPublishException(
        Exception innerException,
        bool ownershipTransferred,
        PlaylistDetailTerminalCommitResult terminalCommitResult)
        : this(innerException, ownershipTransferred)
    {
        TerminalCommitResult = terminalCommitResult;
    }

    internal PlaylistDetailTerminalPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private PlaylistDetailTerminalPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        OwnershipTransferred = info.GetBoolean(nameof(OwnershipTransferred));
    }

    internal bool OwnershipTransferred { get; }

    internal PlaylistDetailTerminalCommitResult TerminalCommitResult { get; }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(OwnershipTransferred), OwnershipTransferred);
    }
}
