# BeMusicSeeker リファクタリング総合計画

## 目的

この計画は、`MainWindowViewModel` の整理を第1弾として進めつつ、続けて着手する価値が高い P0 相当の対象を含め、BeMusicSeeker を長期保守しやすい構造へ移すための入口を定義する。

重視する順序は次の通り。

1. 将来的な `.NET 10` / `net10.0-windows` 移行の阻害要因を減らす。
2. WPF アプリとして、View / ViewModel / Model / service の境界を明確にする。
3. 巨大クラスを facade / coordinator / service / state / presentation model へ分ける。
4. Codex が小さな ticket 単位で自走できる粒度に落とす。
5. ただし、前段リファクタリング後でないと正確な計画を切れないものは、無理に詳細化しない。

## 全体の優先度

| 優先度 | 計画書 | 主対象 | 現時点の実装プラン粒度 | 着手条件 |
|---|---|---|---|---|
| P0-01 | [MainWindowViewModel リファクタリング計画](./P0-01_MainWindowViewModel_リファクタリング計画.md) | `BeMusicSeeker/ViewModels/MainWindowViewModel.cs` | Gate 1〜4 / Ticket Q まで再細分化済み | 第1弾として継続中。2026-07-06 時点で Ticket I-3c まで完了 |
| P0-02 | [BMSLibrary ドメイン facade 化計画](./P0-02_BMSLibrary_ドメインFacade化計画.md) | `BeMusicSeeker/Models/BMSLibrary.cs` | 詳細 ticket 化済み | P0-01 の Gate 2 完了後に限定的な並行着手を再判断。事前調査は並行可 |
| P0-03 | [MainWindow UI / code-behind MVVM 移行計画](./P0-03_MainWindow_UI_MVVM移行計画.md) | `BeMusicSeeker/Views/MainWindow.cs`, `MainWindow.xaml` | 詳細 ticket 化済み | P0-01 の Gate 1 後は子 VM 境界が安定した小領域のみ先行可。大規模 XAML 移行と本格並行は Gate 2 後に再判断 |
| P0-04 | [.NET 10 移行準備と依存関係整理計画](./P0-04_DotNet10_移行準備と依存関係整理計画.md) | `*.csproj`, `libs/`, `native/`, `app.config` | 移行準備は詳細化済み。本移行は後続計画 | 依存棚卸しは即着手可。本移行は P0-01〜03 の主要境界整理後 |
| P1-01 | [BMSPlaylist 責務分割計画](./P1-01_BMSPlaylist_責務分割計画.md) | `BeMusicSeeker/Models/BMSPlaylist.cs` | 中粒度。詳細化は BMSLibrary facade 後 | BMSLibrary の mutation / state 境界整理後 |
| P1-02 | [BmsLibraryInitializationService / scan pipeline 分割計画](./P1-02_BmsLibraryInitializationService_スキャンPipeline分割計画.md) | `BmsLibraryInitializationService.cs` | 中〜詳細 | BMSLibrary 初期化 coordinator の境界確定後 |
| P1-03 | [Settings / テスト基盤 / 境界整備計画](./P1-03_Settings_テスト基盤_境界整備計画.md) | `Settings.Default` 参照、source-text tests、reflection tests | 詳細 ticket 化済み | P0-01 と同時に着手推奨 |
| P2-01 | [DB / LR2 永続化境界整理 後続検討](./P2-01_DB_LR2_永続化境界整理_後続検討.md) | SQLite / LR2 DB / repository | 後続検討 | BMSLibrary / BMSPlaylist 分割後 |
| P2-02 | [BMSFile / Chart domain 分離 後続検討](./P2-02_BMSFile_ChartDomain分離_後続検討.md) | `BMSFile.cs`, `ChartFile*` | 後続検討 | scan pipeline / DB 境界整理後 |
| P2-03 | [CustomTableView 表示基盤整理 後続検討](./P2-03_CustomTableView_表示基盤整理_後続検討.md) | `CustomTableView.cs` | 後続検討 | MainWindow UI split 後 |
| P2-04 | [Native interop / audio / external process 境界 後続検討](./P2-04_NativeInterop_Audio_外部プロセス境界_後続検討.md) | `EverythingNative`, `Win32API`, `Bass`, 外部プレイヤー | 後続検討 | .NET 10 依存棚卸し後 |

補助資料:

- [Codex 共通実行ルール](./00_Codex共通実行ルール.md)
- [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)

## 推奨ロードマップ

### Wave 1: 第1弾。MainWindowViewModel を壊さず分割する

対象: P0-01 と P1-03 の一部。

目的:

- `MainWindowViewModel.cs` を partial / 子 ViewModel / pure service へ移す。
- 既存 XAML binding をすぐ壊さず、root VM は pass-through を持つ。
- source-text test / private reflection test を、分割に耐えるテストへ変更する。

完了条件:

- `MainWindowViewModel.cs` 単体が巨大ファイルでなくなる。P0-01 の Gate 4 では 1,000〜1,500 行以下、または超過分が shell 責務として説明可能な状態を目標にする。
- `MainWindowViewModel` は Shell / composition root に寄り、workflow 詳細は child ViewModel / coordinator / service へ移っている。
- 上部再生パネル、進捗、メイン一覧、プレイリスト、play history、settings、sidebar の責務境界が見える。
- P0-01 計画書の進捗表と Gate 条件を見れば、前提知識なしでも現在地を把握できる。
- 既存の全体 build/test/format/analyzer が通る、または既存警告と新規警告が分類されている。

### Wave 2: ドメイン中核の BMSLibrary を facade 化する

対象: P0-02 と P1-02 の一部。

目的:

- `BMSLibrary` に集中している初期化、スキャン、LR2 song.db 同期、chart_info hydration、install estimation、package install、maintenance、playlist reference、folder rename を coordinator/service へ逃がす。
- lock / state mutation / notification の境界を明文化し、並行処理の回帰を防ぐ。
- `BMSLibrary` は互換 API を持つ facade として残し、内部実装を分解する。

完了条件:

- `BMSLibrary.cs` 本体が facade と state orchestration 中心になる。
- mutation delta / refresh notification / index invalidation が専用 service へ移っている。
- LR2 song.db sync と package install の大きな workflow が root class から独立する。

### Wave 3: MainWindow code-behind / XAML を MVVM に寄せる

対象: P0-03。

目的:

- code-behind は開始時 10,289 行、2026-07-06 時点でも 10,289 行。View 固有の処理、event-command bridge、host 操作に限定していく。
- context menu 生成、URL 解決、ドラッグ&ドロップ、playlist URL download、duplicate group 操作、score viewer 連携を presentation service / command へ移す。
- `MainWindow.xaml` を添付画像の UI 構成に合わせて UserControl 単位へ分割する。

完了条件:

- `MainWindow.cs` は view-specific adapter と lifecycle だけに近づく。
- `MainWindow.xaml` は top playback panel / sidebar / main table / progress overlay へ分割される。
- `async void` は WPF event entry point のみに残り、内部処理は `Task` 化される。

### Wave 4: .NET 10 移行準備を本格化する

対象: P0-04。

目的:

- `net472` 前提、HintPath DLL、native DLL、`app.config` / assembly probing、WPF+WinForms 混在、System.Configuration 依存を棚卸しする。
- `net10.0-windows` への dry-run を可能にする branch / report / blocker list を作る。
- いきなり本移行せず、まず「何が阻害要因か」を testable にする。

完了条件:

- 依存関係台帳が更新されている。
- WPF/WinForms type ambiguity など .NET 10 で source incompatible な箇所が事前修正または明示されている。
- `net10.0-windows` 移行の blocker report が存在し、次の実装計画を切れる。

### Wave 5: P1 / P2 を詳細化する

対象: P1-01, P1-02, P2-01〜04。

この段階では、前段リファクタリングで API 境界・state 境界・UI 境界が変わっているため、現時点の詳細計画に固執しない。各 P1/P2 計画書に「どの完了後に詳細な実装プランを検討するか」を明記している。

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

## この zip の使い方

1. `00_Codex共通実行ルール.md` を読む。
2. P0-01 は進捗表の次未完了 ticket から続ける。2026-07-06 時点の次候補は Ticket I-3d-1。
3. P0-01 の Gate 1 後は、P0-03 の子 VM 境界が安定した小領域だけ先行可とする。P0-01 の Gate 2 が完了したら、P0-02 / P0-03 の大きな並行着手可否を再判断する。P0-01 Gate 4 完了後は、P0-01 起因の制約なしに本格移行できる。
4. P0-04 は棚卸しだけ先行できる。本格的な TFM 変更は P0-01〜03 の主要境界が整った後にする。
5. P1/P2 は前段完了後に、現実のコード形状に合わせて詳細 plan を再作成する。
