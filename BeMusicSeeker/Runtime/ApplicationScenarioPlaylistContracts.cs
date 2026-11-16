#nullable enable
using System.Collections.Immutable;

namespace BeMusicSeeker.Runtime;

/// <summary>通常の外部表インポートへ渡す固定URLです。</summary>
internal sealed record ScenarioPlaylistImportCommand(long RequestId, string Url) : ScenarioCommand(RequestId);
/// <summary>通常の新規作成画面に渡す固定入力です。</summary>
internal sealed record ScenarioPlaylistCreateCommand(long RequestId, string Name, string Symbol) : ScenarioCommand(RequestId);
/// <summary>登録済み表の実ツリー選択を要求します。nullはサマリーです。</summary>
internal sealed record ScenarioPlaylistSelectCommand(long RequestId, int? TableId) : ScenarioCommand(RequestId);
/// <summary>実表示行のMEMOまたはLevelを通常セル確定経路へ渡します。</summary>
internal sealed record ScenarioPlaylistEditCommand(long RequestId, string Md5, string Text, bool Level) : ScenarioCommand(RequestId);
/// <summary>現在の通常行または詳細行を実フォルダ追加・移動へ渡します。</summary>
internal sealed record ScenarioPlaylistAddCommand(long RequestId, int TableId, ImmutableArray<string> Md5s, string? Folder) : ScenarioCommand(RequestId);
/// <summary>通常のフォルダ作成と改名を実行します。</summary>
internal sealed record ScenarioPlaylistFolderCommand(long RequestId, int TableId, string Name) : ScenarioCommand(RequestId);
/// <summary>実propertyの一回の上下操作とOKを要求します。</summary>
internal sealed record ScenarioPlaylistOrderCommand(long RequestId, int TableId, string Folder, bool Down) : ScenarioCommand(RequestId);
/// <summary>既存JSON出力のtyped選択境界へ固定二pathを渡します。</summary>
internal sealed record ScenarioPlaylistExportCommand(long RequestId, int TableId, string HeaderPath, string DataPath) : ScenarioCommand(RequestId);
/// <summary>実設定画面でfont即時保存と未確定path取消を行います。</summary>
internal sealed record ScenarioSettingsEditCommand(long RequestId, double FontSize, string CandidatePath) : ScenarioCommand(RequestId);
/// <summary>実表・実詳細行・表示世代とBindingの値を返します。期待値を作りません。</summary>
internal sealed record ScenarioPlaylistCompleted(long RequestId, ScenarioPlaylistSnapshot Snapshot,
    ImmutableArray<ScenarioImportOutcome> Import = default, ScenarioSettingsEditObservation? SettingsEdit = null) : ScenarioMessage(RequestId);
/// <summary>設定画面の即時保存事実と、実Close前後の未確定候補を一回の観測結果として保持します。</summary>
internal sealed record ScenarioSettingsEditObservation(double ProviderFont, string ProviderPath, string RawFont, string RawPath,
    string CandidateBeforeClose, string CandidateAfterClose);
/// <summary>インポートの実終端後に捕捉した対象別結果です。</summary>
internal sealed record ScenarioImportOutcome(string Uri, string Name, string Kind);
/// <summary>同じ時点の実登録集合、詳細表示、サマリー表示と設定Binding値です。</summary>
internal sealed record ScenarioPlaylistSnapshot(ImmutableArray<ScenarioPlaylistTable> Tables, ImmutableArray<ScenarioPlaylistRow> Rows,
    ImmutableArray<int> SummaryTableIds, int DetailVersion, long SummaryGeneration, double MainFontSize, double SummaryFontSize);
/// <summary>実表の登録identityと保存値です。</summary>
internal sealed record ScenarioPlaylistTable(int Id, string Name, string Symbol, bool ExternalSync, ImmutableArray<string> FolderOrder, ImmutableArray<string> DisplayFolders);
/// <summary>実詳細表示が参照するentry identity、編集列と所持判定です。</summary>
internal sealed record ScenarioPlaylistRow(int TableId, string Md5, string? Sha256, string Title, string Artist,
    string Folder, string? Level, string? Memo, bool Owned);
