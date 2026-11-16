using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 起動時に構成するプレイリストへ、所有ライブラリの能力を接続します。
/// </summary>
internal sealed class BmsPlaylistLibraryBindings
{
    /// <summary>
    /// 一つの所有ライブラリから能力を構成します。
    /// </summary>
    /// <param name="sourceLibrary">プレイリストを所有するライブラリ。</param>
    internal BmsPlaylistLibraryBindings(BMSLibrary sourceLibrary)
    {
        SourceLibrary = sourceLibrary ?? throw new ArgumentNullException(nameof(sourceLibrary));
    }

    /// <summary>
    /// 接続した全能力を所有するライブラリです。
    /// </summary>
    internal BMSLibrary SourceLibrary { get; }

    /// <summary>全guard解放後の同entry/header確定groupを、必要出力前に参照索引へ適用します。</summary>
    internal void ApplyReferenceCommits(IReadOnlyList<PlaylistEntryCommitReceipt> receipts, IReadOnlyList<PlaylistReferenceTableHeader> headers = null)
        => SourceLibrary.ApplyPlaylistReferenceEntryCommits(receipts, headers);
    /// <summary>実表の登録・置換と同じ正本順のheaderを一回で参照索引へ渡します。</summary>
    internal void PublishReferenceTables(IReadOnlyList<PlaylistReferenceTableSnapshot> tables, IReadOnlyList<PlaylistReferenceTableHeader> headers,
        IEnumerable<BMSTable> removals = null) => SourceLibrary.PublishPlaylistReferenceTables(tables, headers, removals);
    /// <summary>選択中のスコア DB を読み直し、推薦用の変更不能な原観測を返します。</summary>
    internal Task<WalkureScoreInput> ReadRecommendationScoresAsync(CancellationToken cancellationToken)
        => SourceLibrary.ReadRecommendationScoresAsync(cancellationToken);

    /// <summary>
    /// 所有ライブラリのbeatoraja出力ハッシュ照合能力を作ります。
    /// </summary>
    /// <returns>所有ライブラリの照合関数。</returns>
    internal Func<BmtSongHashResolveRequest, Tuple<string, string>> CreateBeatorajaBmtSongHashResolver()
        => SourceLibrary.CreateBeatorajaBmtSongHashResolver();

    /// <summary>
    /// 所有ライブラリのLR2プレイリストフォルダ同期能力です。
    /// </summary>
    internal ILr2PlaylistFolderSynchronizationPort Lr2PlaylistFolderSynchronization
        => SourceLibrary.Lr2PlaylistFolderSynchronization;
}
