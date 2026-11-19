using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>統合候補一覧の構築が、ライブラリから受け取る二つの入力です。画面側の管理主体はこの窓口だけでライブラリへ触れます。</summary>
internal interface IDuplicateMergeCandidateListSource
{
    /// <summary>
    /// フォルダ（子孫を含む所持範囲）ごとの導入先候補プロフィールの実在最頻ペアの表示値を、一括で返します。
    /// 有効なペアがないフォルダは両方空です。呼出しはUIスレッド外で行います。
    /// </summary>
    /// <param name="folders">解決するフォルダ。重複は一度だけ解決します。</param>
    /// <param name="cancellationToken">一定件数ごとの境界で確認する取消。</param>
    IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDestinationDisplayPairs(
        IReadOnlyList<string> folders, CancellationToken cancellationToken);

    /// <summary>
    /// 呼出し時点の確定登録を一度だけ捕捉し、フォルダが登録範囲（登録BMSルート本体、または空の登録子ルートを含む祖先）に
    /// 当たるかを判定する述語を返します。手動統合が移動元として拒否するのと同じ判定です。
    /// </summary>
    Func<string, bool> CaptureRegisteredBmsRootPredicate();
}

public partial class BMSLibrary : IDuplicateMergeCandidateListSource
{
    /// <summary>
    /// 読取りロックの保持を長くしないため、このフォルダ数ごとにロックを取り直します。
    /// 一回の取得は既存のプロフィールのキャッシュを共有し、未構築のフォルダだけを所持集合から集計します。
    /// </summary>
    private const int DuplicateMergeCandidateMetadataChunkSize = 256;

    IReadOnlyDictionary<string, (string Title, string Artist)> IDuplicateMergeCandidateListSource.ResolveDestinationDisplayPairs(
        IReadOnlyList<string> folders, CancellationToken cancellationToken)
        => ResolveDuplicateMergeCandidatePairs(folders, cancellationToken);

    /// <summary>インターフェースの実装の本体です。派生が実装を差し替えても、既存の解決へ委譲できるよう分けます。</summary>
    internal IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDuplicateMergeCandidatePairs(
        IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var result = new Dictionary<string, (string Title, string Artist)>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>(DuplicateMergeCandidateMetadataChunkSize);
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || result.ContainsKey(folder)) { continue; }
            result[folder] = (string.Empty, string.Empty);
            pending.Add(folder);
            if (pending.Count >= DuplicateMergeCandidateMetadataChunkSize)
            {
                ResolveDuplicateMergeCandidateChunk(pending, result, cancellationToken);
                pending.Clear();
            }
        }
        if (pending.Count > 0) { ResolveDuplicateMergeCandidateChunk(pending, result, cancellationToken); }
        return result;
    }

    Func<string, bool> IDuplicateMergeCandidateListSource.CaptureRegisteredBmsRootPredicate()
        => CaptureDuplicateMergeCandidateRegisteredPredicate();

    internal Func<string, bool> CaptureDuplicateMergeCandidateRegisteredPredicate()
        => libraryMutationOwner.CaptureRegisteredBmsRootPredicate();

    private void ResolveDuplicateMergeCandidateChunk(
        List<string> chunk, Dictionary<string, (string Title, string Artist)> result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        using (rwlockBMSFiles.GetReaderGuard())
        {
            foreach (string folder in chunk)
            {
                InstallEstimationMetadataProfile profile = ResolveInstallDestinationMetadataProfileUnsafe(folder);
                result[folder] = profile.HasPair
                    ? (profile.DominantPairDisplayTitle ?? string.Empty, profile.DominantPairDisplayArtist ?? string.Empty)
                    : (string.Empty, string.Empty);
            }
        }
    }
}
