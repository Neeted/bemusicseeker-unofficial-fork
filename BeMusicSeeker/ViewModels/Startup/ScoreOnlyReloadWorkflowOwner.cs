using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>スコアだけを再読込みし、呼出元の共通受付を実公開・後片付けの終端まで借用します。</summary>
internal sealed class ScoreOnlyReloadWorkflowOwner
{
    private readonly Func<LibraryFileMutationCapability, Task> reloadScoresAsync;

    /// <summary>同ownerの生存権限を実モデルへ転送する再読込み処理を接続します。</summary>
    /// <param name="reloadScoresAsync">公開と後片付けまで終える実処理。元例外・取消を伝播します。</param>
    internal ScoreOnlyReloadWorkflowOwner(Func<LibraryFileMutationCapability, Task> reloadScoresAsync)
    {
        this.reloadScoresAsync = reloadScoresAsync
            ?? throw new ArgumentNullException(nameof(reloadScoresAsync));
    }

    /// <summary>権限を取り直さず同じ受理済み操作を実行し、元Taskの実終端・失敗・取消を返します。</summary>
    /// <param name="capability">呼出元が終端まで保持する権限。nullは実consumerによる新規受付です。</param>
    internal Task ReloadAsync(LibraryFileMutationCapability capability = null)
        => reloadScoresAsync(capability);
}
