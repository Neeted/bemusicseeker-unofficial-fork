namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>表のLR2カスタムフォルダ出力を止めた理由です。</summary>
internal enum PlaylistOutputStopReason
{
    /// <summary>実効先が他の表と同一または親子になるため、衝突した表を出力対象外にしました。</summary>
    Collision,

    /// <summary>表が登録されていない追加基点を指すため、出力先を決められません。</summary>
    UnregisteredBase
}

/// <summary>出力を止めた表の不変の事実です。通知の表示直前に、選択言語で本文を整形するために必要な値だけを保持します。</summary>
/// <param name="TableName">止めた表の名前。</param>
/// <param name="OutputDirectory">止めた表の実効出力先（絶対パス）。未登録の追加基点では決まらないためnullです。</param>
/// <param name="Reason">止めた理由。</param>
/// <param name="BaseName">未登録の追加基点の名前。衝突ではnullです。</param>
internal sealed record PlaylistStoppedOutput(string TableName, string OutputDirectory, PlaylistOutputStopReason Reason, string BaseName = null);
