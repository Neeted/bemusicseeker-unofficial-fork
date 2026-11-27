using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 起動時の更新処理へ渡す、起動時の初期化の結果を作ります。初期化の合流を対象にしないケースは、
/// 初期化が最初から成功している結果を使い、更新ツールの起動条件だけを検証します。
/// </summary>
internal static class StartupUpdateInitializationTestSupport
{
    /// <summary>必須処理と完了の公開に成功し、失敗を伴わない初期化の結果です。</summary>
    internal static StartupInitializationResult Succeeded { get; } =
        new(StartupInitializationOutcome.Succeeded, CompletionPublished: true);

    /// <summary>最初から成功している初期化の観測結果を返します。</summary>
    internal static Task<StartupInitializationResult> SucceededTask() => Task.FromResult(Succeeded);
}
