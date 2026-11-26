using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models;

/// <summary>重複音源フォルダ一つの確認の入力です。</summary>
/// <param name="Folder">確認する対象フォルダ。</param>
/// <param name="IsOtherTargetFolder">
/// 所有範囲の列挙中に見つけた配下のディレクトリのパスを受け、別の対象フォルダなら真を返す判定です。真のディレクトリ以下は所有範囲から除きます。
/// 確認の開始時に捕捉した所持集合から作ります（<see cref="CreateTargetFolderPredicate"/>）。省略（null）なら配下の別の対象フォルダを除きません。
/// </param>
/// <param name="CancellationToken">取消の合図。衝突名の判定の前後で確認し、<see cref="OperationCanceledException"/> を送出します。</param>
internal sealed record AudioDuplicateInspectionRequest(
    string Folder,
    Func<string, bool> IsOtherTargetFolder = null,
    CancellationToken CancellationToken = default)
{
    /// <summary>
    /// 捕捉した対象フォルダの集合から、<see cref="IsOtherTargetFolder"/> に使う判定を作ります。
    /// 比較は大文字小文字を区別せず、パスは <see cref="DuplicateMergeSafetyPolicy.NormalizeFolder"/> でそろえます。確認するフォルダ自身は、配下の判定の対象ではありません。
    /// </summary>
    /// <param name="targetFolders">確認の開始時に捕捉した所持集合の、すべての対象フォルダ。</param>
    internal static Func<string, bool> CreateTargetFolderPredicate(IEnumerable<string> targetFolders)
    {
        ArgumentNullException.ThrowIfNull(targetFolders);
        var set = new HashSet<string>(
            targetFolders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Select(DuplicateMergeSafetyPolicy.NormalizeFolder),
            StringComparer.OrdinalIgnoreCase);
        return directory => set.Contains(DuplicateMergeSafetyPolicy.NormalizeFolder(directory));
    }
}

/// <summary>重複音源フォルダの確認結果の段階です。</summary>
internal enum AudioDuplicateInspectionStage
{
    /// <summary>整理不可でなく、すべての衝突名を同等と判定しました。衝突名が0件の場合を含みます。</summary>
    Safe,

    /// <summary>整理不可でなく、同等と判定しなかった衝突名が一つ以上あります。</summary>
    Caution,

    /// <summary>リンクまたは解析不能の理由があります。衝突名の一覧と同等の一覧は空です。</summary>
    Unorganizable
}

/// <summary>重複音源フォルダ一つの確認結果です。不変の値です。</summary>
/// <param name="Stage">段階。</param>
/// <param name="Reasons">整理不可の理由。<see cref="DuplicateMergeVerificationReasonKind.Link"/> と <see cref="DuplicateMergeVerificationReasonKind.Unreadable"/> のどちらか一つだけで、整理不可でなければ空です。</param>
/// <param name="CollisionNames">実フォルダから確定した衝突名。大文字小文字を無視した昇順で、書式は <see cref="AudioDuplicateFolder.CollisionNames"/> と同じです。整理不可では空です。</param>
/// <param name="EquivalentCollisionNames">同等と判定した衝突名。<paramref name="CollisionNames"/> の部分集合で、同じ並びです。整理不可では空です。</param>
internal sealed record AudioDuplicateFolderInspection(
    AudioDuplicateInspectionStage Stage,
    IReadOnlyList<DuplicateMergeVerificationReason> Reasons,
    IReadOnlyList<string> CollisionNames,
    IReadOnlyList<string> EquivalentCollisionNames)
{
    /// <summary>同等と判定しなかった衝突名を、<see cref="CollisionNames"/> の並びで返します。</summary>
    internal IEnumerable<string> NonEquivalentCollisionNames
    {
        get
        {
            var equivalent = new HashSet<string>(EquivalentCollisionNames, StringComparer.OrdinalIgnoreCase);
            return CollisionNames.Where(name => !equivalent.Contains(name));
        }
    }
}

/// <summary>重複音源の確認の検査器の窓口です。手順の主体は、この窓口だけで検査を行います。</summary>
internal interface IAudioDuplicateFolderInspector
{
    /// <summary>一つの対象フォルダを検査します。UIスレッド外で呼びます。ファイルとDBを変更しません。</summary>
    AudioDuplicateFolderInspection Inspect(AudioDuplicateInspectionRequest request);
}

/// <summary>
/// 重複音源の確認の検査器です。対象フォルダの所有範囲を一回列挙して衝突名を実フォルダから確定し、各衝突名の WAV と OGG の同等性を判定します。
/// ファイルを読むだけで、何も変更しません。
/// </summary>
/// <remarks>
/// 同等性は統合の安全性確認と同じ判定（<see cref="DuplicateMergeAudioNameJudge"/>）を使います。復号できない音源は同等でないものとして扱い、
/// 読取りの入出力の失敗は整理不可（解析不能）にします。音声ランタイムの致命的な失敗などの予期しない例外は呼出元へ伝えます。
/// 一つのフォルダの中では、衝突名を論理プロセッサ数を上限に並列で処理します。モデルのロックを保持したまま、ファイルを読みません。
/// </remarks>
internal sealed class AudioDuplicateFolderInspector : IAudioDuplicateFolderInspector
{
    private sealed record CollisionEntry(string Name, DuplicateMergeScannedFile Wav, DuplicateMergeScannedFile Ogg);

    /// <inheritdoc />
    public AudioDuplicateFolderInspection Inspect(AudioDuplicateInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string folder = request.Folder;
        CancellationToken cancellationToken = request.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        var scan = DuplicateMergeFolderScan.Scan(folder, descend: true, request.IsOtherTargetFolder);
        // 本体がリンクなら走査は配下へ入らず、リンクは空の相対パスだけです。配下のリンクは理由にせず、先へ入らず音源として数えません。
        if (scan.Links.Contains(string.Empty))
        {
            return Unorganizable(DuplicateMergeVerificationReasonKind.Link, [new DuplicateMergeFinding(folder, string.Empty)]);
        }
        if (scan.Failures.Count > 0)
        {
            return Unorganizable(
                DuplicateMergeVerificationReasonKind.Unreadable,
                scan.Failures.Select(failure => new DuplicateMergeFinding(folder, failure)));
        }

        CollisionEntry[] collisions = FindCollisions(scan);
        if (collisions.Length == 0)
        {
            return new AudioDuplicateFolderInspection(AudioDuplicateInspectionStage.Safe, [], [], []);
        }

        DuplicateMergeAudioReader.EnsureRuntime();
        var judgements = new DuplicateMergeAudioNameJudge.Result[collisions.Length];
        DuplicateMergeAudioNameJudge.RunParallel(
            collisions.Length,
            index => judgements[index] = DuplicateMergeAudioNameJudge.Judge(
                [(collisions[index].Wav.FullPath, true), (collisions[index].Ogg.FullPath, false)]),
            cancellationToken);

        var unreadable = new List<DuplicateMergeFinding>();
        for (int index = 0; index < collisions.Length; index++)
        {
            foreach (int fileIndex in judgements[index].UnreadableIndexes)
            {
                DuplicateMergeScannedFile file = fileIndex == 0 ? collisions[index].Wav : collisions[index].Ogg;
                unreadable.Add(new DuplicateMergeFinding(folder, file.RelativePath));
            }
        }
        if (unreadable.Count > 0)
        {
            return Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, unreadable);
        }

        string[] names = [.. collisions.Select(collision => collision.Name)];
        string[] equivalent = [.. Enumerable.Range(0, collisions.Length).Where(index => judgements[index].Equivalent).Select(index => names[index])];
        return new AudioDuplicateFolderInspection(
            equivalent.Length == names.Length ? AudioDuplicateInspectionStage.Safe : AudioDuplicateInspectionStage.Caution,
            [],
            names,
            equivalent);
    }

    private static AudioDuplicateFolderInspection Unorganizable(
        DuplicateMergeVerificationReasonKind kind, IEnumerable<DuplicateMergeFinding> findings)
        => new(
            AudioDuplicateInspectionStage.Unorganizable,
            [new DuplicateMergeVerificationReason(kind, DuplicateMergeGroupInspection.Sort(findings.Distinct()))],
            [],
            []);

    /// <summary>
    /// 走査したファイルから衝突名を求めます。同じディレクトリで拡張子なしの名前が一致する <c>.wav</c> と <c>.ogg</c> の組で、
    /// 拡張子なしの名前が空のファイルは数えません。名前の表記は <c>.wav</c> のファイルのもので、大文字小文字を無視した昇順に並べます。
    /// </summary>
    private static CollisionEntry[] FindCollisions(DuplicateMergeFolderScan scan)
    {
        var wavs = new Dictionary<string, (string Name, DuplicateMergeScannedFile File)>(StringComparer.OrdinalIgnoreCase);
        var oggs = new Dictionary<string, DuplicateMergeScannedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateMergeScannedFile file in scan.Files.Values)
        {
            string extension = Path.GetExtension(file.RelativePath);
            bool isWav = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
            if (!isWav && !extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string baseName = Path.GetFileNameWithoutExtension(file.RelativePath);
            if (baseName.Length == 0)
            {
                continue;
            }
            string directory = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
            string name = directory.Length == 0 ? baseName : directory + "\\" + baseName;
            if (isWav) { wavs[name] = (name, file); }
            else { oggs[name] = file; }
        }
        return [.. wavs
            .Where(pair => oggs.ContainsKey(pair.Key))
            .Select(pair => new CollisionEntry(pair.Value.Name, pair.Value.File, oggs[pair.Key]))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
