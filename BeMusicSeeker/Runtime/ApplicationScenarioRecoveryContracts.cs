#nullable enable
namespace BeMusicSeeker.Runtime;

/// <summary>起動前に宣言したschema確認の実到達だけを待ちます。</summary>
internal sealed record ScenarioSchemaConfirmationCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>実確認へ到達した後、一回だけ明示回答を解放します。</summary>
internal sealed record ScenarioSchemaAnswerCommand(long RequestId, bool Approve) : ScenarioCommand(RequestId);
/// <summary>実確認の到達、または回答を解放する前のACKです。</summary>
internal sealed record ScenarioRecoveryAcknowledged(long RequestId) : ScenarioMessage(RequestId);
/// <summary>実設定画面のSQL保存または復元ハンドラーへ渡す固定pathです。</summary>
internal sealed record ScenarioPlaylistSqlCommand(long RequestId, string Path, bool Restore) : ScenarioCommand(RequestId);
/// <summary>実サマリーの表削除へ渡す登録済みIDです。</summary>
internal sealed record ScenarioPlaylistRemoveCommand(long RequestId, int TableId) : ScenarioCommand(RequestId);
