# BeMusicSeeker リファクタリング総合計画

## 目的

この計画は、BeMusicSeeker を長期保守しやすい構造へ移すための Refactoring MVP を定義する。

現在の最優先は、DTO 単体導入や checkpoint の連続ではなく、MVVM としての責務分離と巨大コード整理を進めること。以後の実装単位は **1 workflow / 1 responsibility boundary** とし、成果は root ViewModel / code-behind / facade から責務が減ったことで測定する。

## 最初に読むファイル

1. [Codex 共通実行ルール](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS](./PLAN_STATUS.md)
3. active lane の対象計画書
4. [現状メトリクス調査メモ](./99_調査メモ_現状メトリクス.md)

## Release Freeze

Refactoring MVP Gate 通過まで、リリース作業を凍結する。詳細は [Codex 共通実行ルール](./00_Codex共通実行ルール.md) と [REF-MVP Release Freeze and Gate Decision](./decisions/REF-MVP_release_freeze_and_gate.md) を正本とする。

禁止:

- `Properties/AssemblyInfo.cs` の `AssemblyInformationalVersion` 更新。
- `release notes/` の新規リリース向け更新。
- `version.txt` のリリース目的更新。
- `scripts/publish.ps1` / `scripts/release.ps1` のリリース運用目的変更。
- GitHub Release、Git tag、Release draft、配布 package の作成。
- ユーザーに「リリース準備完了」と見える変更。

例外:

- build / test のためのローカル artifact 作成。
- release freeze ルール自体を文書化する docs 変更。
- 既存 release 関連コードの refactor blocker 調査。

## Refactoring MVP Gate

MVP Gate は「リファクタリングがある程度完了し、その後は純粋に `.NET 10` / `net10.0-windows` 移行作業として検討できる段階」を指す。

通過条件:

- `MainWindowViewModel` は application shell / composition / lifecycle / dialog request bridge / progress bridge / UI thread 境界 / child ViewModel・coordinator 委譲に寄っている。
- main chart list、playlist detail build / apply、play history、playback、settings save、library refresh、package install workflow が root から child ViewModel / coordinator / service へ移っている。
- `MainWindow.cs` は WPF lifecycle / view-host / WPF event entry point / UI 型 adapter / command invocation bridge に寄っている。
- 処理本体を持つ `async void` と巨大 event handler が大幅に減っている。
- `BMSLibrary` は public compatibility facade として残しつつ、initialization、LR2 `song.db` sync、package install、maintenance、playlist reference、file operation、normal library refresh、source text / source file handling が service / coordinator へ移っている。
- 新規 service / coordinator が `Window`、`Control`、`MessageBox`、`Settings.Default`、`NLog`、無制御な DB connection / transaction、無制御な `Dispatcher` 直参照を増やしていない。
- `.NET 10` 移行 blocker が、巨大クラス内の未整理ロジックではなく adapter / gateway / native dependency / config / settings / external process host の課題として説明できる。
- build、test、format / whitespace check、`git diff --check` が通る。
- サブエージェント静的レビューで重大な指摘がない。

## 行数 Guardrail

行数は設計成功の唯一の判定にしない。ただし巨大クラス整理の進捗監視として使う。

| 対象 | 現状目安 | Guardrail |
|---|---:|---:|
| `MainWindowViewModel.cs` | 23,443 行 | 8,000 行以下 |
| `MainWindow.cs` | 10,289 行 | 5,000 行以下 |
| `BMSLibrary.cs` | 17,577 行 | 12,000 行以下 |
| `BMSPlaylist.cs` | P1 対象 | P0/P1 境界で 6,000 行以下 |

Guardrail を超える場合は `PLAN_STATUS.md` に、残す責務、残す理由、次の extraction 候補、サブエージェントレビュー結果を記録する。

## Active Lanes

`PLAN_STATUS.md` を正本として、次の active lanes を並行可能な MVP 作業として扱う。Codex は毎回、MVP Gate に最も近づく slice を選ぶ。

| Lane | 計画 | 目的 | 直近 ticket |
|---|---|---|---|
| A | [P0-01 MainWindowViewModel](./P0-01_MainWindowViewModel_リファクタリング計画.md) | root ViewModel を shell / composition / lifecycle に寄せる | `REF-MVP-A1` completed checkpoint / 後続は workflow 単位で再計画 |
| B | [P0-03 MainWindow UI / code-behind](./P0-03_MainWindow_UI_MVVM移行計画.md) | code-behind を view-host / command bridge に寄せる | `REF-MVP-B1` completed checkpoint / 後続は workflow 単位で再計画 |
| C | [P0-02 BMSLibrary facade](./P0-02_BMSLibrary_ドメインFacade化計画.md) | `BMSLibrary` を compatibility facade として薄くする | `REF-MVP-C74` completed checkpoint / 次は `REF-MVP-C75` maintenance dispatch reflection boundary review |
| D | [P0-04 .NET 10 readiness](./P0-04_DotNet10_移行準備と依存関係整理計画.md) | TFM を変えず migration blocker を devdocs に inventory 化する | `REF-MVP-D1` completed checkpoint / 後続は boundary 単位で再計画 |

推奨する最初の実装順:

1. Lane C 後続候補: `REF-MVP-C75` として C74 後に残る `DispatchMaintenanceHydrationResult` private reflection test を direct plan / adapter test へ寄せるか、dispatch bridge 自体を service seam にするかを 1 件に絞る。
2. Lane B 後続候補: `REF-MVP-B1` 完了後の duplicate / external URL / score viewer / drag-drop / CustomTable adapter などを workflow 単位で再計画。
3. Lane A 後続候補: `REF-MVP-A1` 完了後の playlist source build stage、play history、playback、settings save、library refresh などを P0-03 連動条件も見て再計画。
4. Lane D 後続候補: `REF-MVP-D1` inventory 完了後の Settings boundary、native load layout、WPF / WinForms boundary などを 1 件だけ active ticket 化。

## Ticket 粒度

- 1 ticket は 1 workflow / 1 responsibility boundary を移す。
- DTO 追加、method signature 整理、result object 導入だけで 1 ticket にしない。
- DTO、result object、helper、adapter は成果ではなく手段として扱う。
- checkpoint 専用 ticket を連続させない。
- decision record は、永続化、DB schema、public API、concurrency / lock ordering、UI observable behavior、.NET migration policy に関わる場合だけ作る。
- docs-only commit は計画修正、gate 定義、重要 decision record に限る。

## P0 の扱い

### Lane A: MainWindowViewModel shell 化

`MainWindowViewModel` は app shell / composition root として残し、playlist、main chart list、play history、playback、settings、library refresh の workflow を child ViewModel / coordinator / service へ移す。

旧 `L-3c-17: Playlist main view apply result DTO 導入` は独立 ticket ではなく、`REF-MVP-A1` の subtask に格下げする。

### Lane B: MainWindow code-behind / XAML MVVM 移行

`MainWindow.cs` の巨大 event handler と処理本体を持つ `async void` を減らす。`tableContextMenuOpened` は最初の command bridge slice として扱う。

### Lane C: BMSLibrary domain facade 化

`BMSLibrary` は compatibility facade として残し、source-text / private reflection tests を先に分割耐性のある形へ緩める。その後、partial split または既存 `BmsLibraryInternal` service への workflow 移動を進める。

### Lane D: .NET 10 migration readiness

Refactoring MVP Gate 通過前に `net10.0-windows` 本移行は行わない。TFM を変えない blocker inventory、adapter / gateway 化、dependency cleanup、migration readiness の明文化に限定する。

## P1 / P2 の扱い

P1 / P2 は MVP Gate の進捗を見て再計画する。

- P1-01 `BMSPlaylist` は P0 / P1 境界で 6,000 行以下を guardrail とし、BMSLibrary / playlist workspace 境界が安定した後に詳細化する。
- P1-02 scan pipeline は P0-02 の facade / initialization boundary が安定した後に詳細化する。
- P1-03 Settings / テスト基盤は P0 ticket の blocker 解消として必要になった箇所から扱う。
- P2 は該当する P0/P1 の完了後に再計画する。

## 横断方針

- View 固有の `Window`, `Control`, `Panel`, `ContextMenu`, `TreeViewItem`, `DragEventArgs` は View / behavior / adapter で閉じる。
- `Settings.Default`、`System.Configuration`、`app.config`、HintPath DLL、native DLL copy、WPF + WinForms 同時参照、P/Invoke、外部 process host は migration risk として adapter / gateway へ寄せる。
- 巨大ファイル前提の source-text tests は、分割後の複数ファイルを読む helper に置き換える。
- private reflection tests は、抽出した service / internal API の挙動テストへ段階的に置き換える。
