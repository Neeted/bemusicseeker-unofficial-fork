# BeMusicSeeker リファクタリング総合計画

## 目的

この計画は、BeMusicSeeker を長期保守しやすい構造へ移すための入口を定義する。

重視する順序は次の通り。

1. 将来的な `.NET 10` / `net10.0-windows` 移行の阻害要因を減らす。
2. WPF アプリとして、View / ViewModel / Model / service の境界を明確にする。
3. 巨大クラスを facade / coordinator / service / state / presentation model へ分ける。
4. Codex が小さな ticket 単位で自走できる粒度に落とす。
5. 前段リファクタリング後でないと正確な計画を切れないものは、無理に詳細化しない。

## 最初に読むファイル

1. [Codex 共通実行ルール](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS](./PLAN_STATUS.md)
3. その時点の active ticket を持つ P0 計画書
4. [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)

## 現在地

2026-07-06 再ベースライン。

| 計画 | 状態 | 次に見る場所 |
|---|---|---|
| P0-01 | L-3c-8-checkpoint まで完了。次は L-3c-9 で rebuilt source view apply result DTO を導入する | [P0-01](./P0-01_MainWindowViewModel_リファクタリング計画.md) |
| P0-02 | P0-01 完了待ちではない。source-text helper / reflection inventory / dependency inventory / partial split 計画は即並行可 | [P0-02](./P0-02_BMSLibrary_ドメインFacade化計画.md) |
| P0-03 | P0-01 の root pass-through 削除に必要な前提。event handler inventory と XAML DataContext 移行準備は早期着手可 | [P0-03](./P0-03_MainWindow_UI_MVVM移行計画.md) |
| P0-04 | TFM 変更なしの棚卸しは即並行可。本格的な `net10.0-windows` 移行は後続計画 | [P0-04](./P0-04_DotNet10_移行準備と依存関係整理計画.md) |

## 全体の優先度

| 優先度 | 計画書 | 主対象 | 現時点の実装プラン粒度 | 着手条件 |
|---|---|---|---|---|
| P0-01 | [MainWindowViewModel リファクタリング計画](./P0-01_MainWindowViewModel_リファクタリング計画.md) | `BeMusicSeeker/ViewModels/MainWindowViewModel.cs` | active ticket 1 件 + 次候補 2 件へ整理済み | 継続中。次は L-3c-9 |
| P0-02 | [BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md) | `BeMusicSeeker/Models/BMSLibrary.cs` | Phase 0 は着手可。後半は P0-01 / P0-04 の結果を見て再計画 | BMSLIB-0A/0B と依存棚卸しは即並行可 |
| P0-03 | [MainWindow UI / code-behind MVVM 移行計画](./P0-03_MainWindow_UI_MVVM移行計画.md) | `BeMusicSeeker/Views/MainWindow.cs`, `MainWindow.xaml` | Phase 0 は着手可。XAML 分割は child VM 境界に合わせて段階化 | MWUI-0B と DataContext 移行準備は即並行可 |
| P0-04 | [.NET 10 移行準備と依存関係整理計画](./P0-04_DotNet10_移行準備と依存関係整理計画.md) | `*.csproj`, `libs/`, `native/`, `app.config` | 棚卸しは詳細化済み。本移行は後続計画 | TFM を変えない inventory は即並行可 |
| P1-01 | [BMSPlaylist 責務分割計画](./P1-01_BMSPlaylist_責務分割計画.md) | `BeMusicSeeker/Models/BMSPlaylist.cs` | 中粒度 | BMSLibrary / playlist workspace 境界が安定した後に詳細化する |
| P1-02 | [BmsLibraryInitializationService / scan pipeline 分割計画](./P1-02_BmsLibraryInitializationService_スキャンPipeline分割計画.md) | `BmsLibraryInitializationService.cs` | 中粒度 | P0-02 Phase 0 完了後に詳細な実装プランを検討する |
| P1-03 | [Settings / テスト基盤 / 境界整備計画](./P1-03_Settings_テスト基盤_境界整備計画.md) | `Settings.Default` 参照、source-text tests、reflection tests | 横断支援計画 | P0 ticket の blocker が明確になったところから必要最小限で着手する |
| P2-01 | [DB / LR2 永続化境界整理 後続検討](./P2-01_DB_LR2_永続化境界整理_後続検討.md) | SQLite / LR2 DB / repository | 後続検討 | BMSLibrary / BMSPlaylist 分割後に詳細な実装プランを検討する |
| P2-02 | [BMSFile / Chart domain 分離 後続検討](./P2-02_BMSFile_ChartDomain分離_後続検討.md) | `BMSFile.cs`, `ChartFile*` | 後続検討 | scan pipeline / DB 境界整理後に詳細な実装プランを検討する |
| P2-03 | [CustomTableView 表示基盤整理 後続検討](./P2-03_CustomTableView_表示基盤整理_後続検討.md) | `CustomTableView.cs` | 後続検討 | MainWindow UI split 後に詳細な実装プランを検討する |
| P2-04 | [Native interop / audio / external process 境界 後続検討](./P2-04_NativeInterop_Audio_外部プロセス境界_後続検討.md) | `EverythingNative`, `Win32API`, `Bass`, 外部プレイヤー | 後続検討 | P0-04 inventory 後に詳細な実装プランを検討する |

補助資料:

- [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)
- [P0-01 完了履歴](./P0-01_MainWindowViewModel_完了履歴.md)

## P0 の進め方

### P0-01: MainWindowViewModel

P0-01 は Gate 4 まで完了してから他 P0 へ進む gate ではない。最小完了条件は次の通り。

- root nested presentation contract 依存が縮小し、child VM / service が root type を契約型置き場として使わない。
- main chart list と playlist の主要 workflow は child VM / coordinator / service へ移り、root は shell / composition / lifecycle / dialog request に寄る。
- root pass-through 削除に必要な XAML / code-behind DataContext 移行条件が P0-03 側で明確になっている。
- 残る root の行数超過分を責務として説明できる。

### P0-02: BMSLibrary

P0-02 は P0-01 の完了待ちにしない。BMSLibrary の source-text helper、private reflection inventory、dependency inventory、partial split 計画は UI と衝突しにくいため即並行できる。

ただし、state mutation / lock / notification を大きく動かす ticket は、BMSLibrary 側の安全網と P0-04 の dependency inventory が揃ってから詳細化する。

### P0-03: MainWindow UI

P0-03 は P0-01 後半の blocker になり得る。root pass-through 削除には XAML / code-behind が child VM or service を直接参照できる状態が必要になる。

早期着手は次に限定する。

- MainWindow event handler inventory。
- `async void` と処理本体の分類。
- XAML DataContext 移行対象の棚卸し。
- playback / progress / main workspace の UserControl 化準備。

### P0-04: .NET 10 移行準備

P0-04 は TFM 変更なしの棚卸しを即並行する。本格的な `net10.0-windows` 移行は、P0-01 / P0-03 の UI 境界と dependency inventory が揃った後に dry-run branch と blocker report を作ってから決める。

即時対象:

- `BeMusicSeeker.csproj` / tests / updater / tools の TargetFramework, UseWPF, UseWindowsForms, HintPath, native copy, `System.Configuration` 参照。
- `app.config` probing / userSettings / AppContextSwitchOverrides。
- WPF + WinForms 混在による `MenuItem` / `ContextMenu` ambiguity。
- `Settings.Default` / `System.Configuration` 依存の境界。

## P1 / P2 の扱い

P1 / P2 は詳細化しすぎない。P0 で API 境界・state 境界・UI 境界が変わるため、現時点の細かい ticket に固執しない。

- P1-01 は P0-02 の mutation / facade 境界が見えた後に詳細な実装プランを検討する。
- P1-02 は P0-02 Phase 0 と initialization dependency inventory 完了後に詳細な実装プランを検討する。
- P1-03 は P0 ticket の blocker 解消として必要になった箇所から扱い、settings wrapper の owner は P0-04 / P1-03 で揃える。
- P2 は該当する P0/P1 の完了後に再計画する。

## 横断的な設計方針

### MVVM 境界

- ViewModel は UI 状態と command の公開に集中する。
- View 固有の `Window`, `Control`, `Panel`, `ContextMenu`, `TreeViewItem`, `DragEventArgs` は View / behavior / adapter で閉じる。
- Model は永続化行とドメイン状態を分ける。
- `MainWindowViewModel` は app shell / composition root、`BMSLibrary` は domain facade として残してよいが、workflow の詳細は service へ移す。

### .NET 10 移行境界

- `Settings.Default`、`System.Configuration`、`app.config`、HintPath DLL、native DLL copy、WPF+WinForms 同時参照、P/Invoke、外部 process host は移行リスクとして扱う。
- すぐに全廃せず、wrapper / adapter / gateway を作って依存箇所を狭める。
- `net472` と `net10.0-windows` の両立を無理に約束しない。まず移行 blocker を可視化し、その後に本移行計画を切る。

### テスト方針

- 巨大ファイル前提の source-text tests は、分割後の複数ファイルを読む helper に置き換える。
- private reflection tests は、抽出した pure service / internal API の挙動テストへ段階的に置き換える。
- 並行処理、DB mutation、file system mutation、Settings.Default、WPF Dispatcher を触るテストは、共有状態の復元と非並列化を明示する。
