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
    Task<UiInteractionResult<Lr2PlayHistorySchemaUninstallMode>> ShowAsync(
        string scoreDbPath,
        CancellationToken cancellationToken = default);
}
