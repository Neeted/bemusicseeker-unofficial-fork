using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models;

/// <summary>重複音源の探索が進んでいる段階です。件数は持ちません。</summary>
internal enum AudioDuplicateSearchStage
{
    /// <summary>登録BMSルート配下の <c>.wav</c> と <c>.ogg</c> を列挙し、同名の組を集めています。</summary>
    Enumerate,

    /// <summary>組を対象フォルダへ割り当て、結果を組み立てています。</summary>
    Aggregate
}

/// <summary>重複音源の探索の進捗です。段階だけを示す不定進捗で、件数は報告しません。</summary>
/// <param name="Stage">進行中の段階。</param>
internal readonly record struct AudioDuplicateSearchProgress(AudioDuplicateSearchStage Stage);

/// <summary>
/// 重複音源フォルダ（同名の WAV と OGG を持つ対象フォルダ）一つの探索結果です。
/// 一フォルダの <see cref="DuplicateGroup"/> と、探索時点の衝突名の一覧を持ちます。
/// </summary>
/// <remarks>
/// <see cref="Group"/> のインスタンスは、状態（選択・確認結果）の対応付けの鍵です。衝突名の集合が変わらない限り、
/// 探索し直しでも同じインスタンスを引き継ぎます。インスタンスは不変です。
/// </remarks>
internal sealed class AudioDuplicateFolder
{
    /// <summary>フォルダと、その直下の所持譜面を持つ一フォルダのグループ。</summary>
    internal AudioDuplicateFolder(DuplicateGroup group, IReadOnlyList<string> collisionNames)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        CollisionNames = collisionNames ?? throw new ArgumentNullException(nameof(collisionNames));
    }

    /// <summary>フォルダ一つのグループ。<see cref="DuplicateGroup.Folders"/> は一件で、譜面はフォルダ直下の所持譜面のすべてです。</summary>
    internal DuplicateGroup Group { get; }

    /// <summary>フォルダのパス。表記は捕捉した所持集合の行の順で最初に現れたものです。</summary>
    internal string Folder => Group.Folders[0];

    /// <summary>
    /// 探索時点の衝突名の一覧。大文字小文字を無視した昇順で、対象フォルダからの相対ディレクトリと拡張子なしの名前を <c>\</c> でつないだ文字列です。
    /// </summary>
    internal IReadOnlyList<string> CollisionNames { get; }
}
