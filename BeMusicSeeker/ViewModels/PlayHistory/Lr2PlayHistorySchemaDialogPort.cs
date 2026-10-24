using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// LR2 play history schema dialog を settings workflow へ返す technology-neutral port です。
/// </summary>
internal interface ILr2PlayHistorySchemaUninstallDialogPort
{
    /// <summary>既存の撤去範囲選択へ候補破棄案内を併記し、同じ一回の承認・取消を返します。</summary>
    /// <param name="candidateDiscardNotice">実受付後に戻す環境候補の案内。受理済み修復情報は含めません。</param>
    Task<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> ShowAsync(
        string scoreDbPath,
        CancellationToken cancellationToken = default, string candidateDiscardNotice = null);
}
