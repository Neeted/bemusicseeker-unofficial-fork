using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>要求開始時に捕捉した保留の元入力範囲です。delete_parentによる追加整理範囲は含めません。</summary>
/// <param name="Package">後から元パスが変わっても本人を識別する保留参照です。</param>
/// <param name="Path">捕捉した元入力パスです。</param>
/// <param name="IsDirectory">捕捉時にディレクトリ入力だったかどうかです。</param>
internal sealed record PendingInstallInputReference(ChartPackage Package, string Path, bool IsDirectory);

/// <summary>開始時の別保留入力を追加残存整理から保護します。本人の通常移動・消費には適用しません。</summary>
/// <param name="originalPackage">work packageと区別して、処理本人を識別する元保留です。</param>
/// <param name="inputReferences">操作開始時に一回捕捉し、成功後も再計画しない全保留の元入力です。</param>
internal sealed class PendingInstallInputProtection(
    ChartPackage originalPackage,
    IReadOnlyList<PendingInstallInputReference> inputReferences)
{
    /// <summary>捕捉済みの別保留の単体入力またはディレクトリ本体・配下が候補を参照するかを返します。</summary>
    internal bool IsReferencedByOtherPackage(string candidatePath)
    {
        foreach (PendingInstallInputReference input in inputReferences)
        {
            if (ReferenceEquals(input.Package, originalPackage)) { continue; }
            // 保留公開とFS列挙が返す通常の絶対パスを、既存の要素境界判定で比較します。
            if (input.IsDirectory
                ? LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(candidatePath, input.Path)
                : string.Equals(candidatePath, input.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
