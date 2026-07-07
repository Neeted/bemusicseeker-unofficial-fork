# PLAN_STATUS

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

最終更新日: 2026-07-07

## Refactoring MVP State

Release Freeze: active。

`git push`、Git tag、GitHub Release、Release draft、publish / release script 実行は禁止。Refactoring MVP Gate 通過前に release / version 関連ファイルは原則触らない。

## Active Lanes

| Lane | Active ticket | 状態 | 次に読む |
|---|---|---|---|
| A: MainWindowViewModel shell 化 | `REF-MVP-A1: Playlist detail build workflow extraction` | implementation in review | [P0-01](./P0-01_MainWindowViewModel_リファクタリング計画.md) |
| B: MainWindow code-behind / XAML MVVM 移行 | `REF-MVP-B1: MainWindow event handler inventory and first command bridge` | completed checkpoint | [P0-03](./P0-03_MainWindow_UI_MVVM移行計画.md) |
| C: BMSLibrary domain facade 化 | `REF-MVP-C1: BMSLibrary source-text helper and facade split foundation` | completed checkpoint | [P0-02](./P0-02_BMSLibrary_ドメインFacade化計画.md) |
| D: .NET 10 migration readiness | `REF-MVP-D1: .NET 10 blocker inventory in devdocs` | active docs/inventory lane | [P0-04](./P0-04_DotNet10_移行準備と依存関係整理計画.md) |

Codex は毎回、Refactoring MVP Gate に最も近づく slice を選ぶ。現時点の推奨順は Lane A → Lane D → Lane C 後続候補 → Lane B 後続候補。

## Ticket Details

### `REF-MVP-A1: Playlist detail build workflow extraction`

目的:

- playlist detail build の request scheduling、source build、view apply、main view apply、finalize timing を、DTO 単位ではなく workflow 単位で root から coordinator / workspace へ移す。
- root `MainWindowViewModel` は request 発行、lifecycle、dialog / progress bridge、UI thread 境界だけを持つ。
- 挙動変更、XAML binding 変更、serialized value 変更、DB schema 変更はしない。

完了条件:

- playlist detail build workflow の主要処理が root から移動している。
- root 側に残る処理が orchestration / bridge として説明できる。
- 既存 UI 挙動が変わらない。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-B1: MainWindow event handler inventory and first command bridge`

状態: completed checkpoint。Lane B 後続は workflow 単位で再計画する。

目的:

- `MainWindow.cs` の巨大 event handler と `async void` を分類する。
- `tableContextMenuOpened` の state calculation を presentation service / command args DTO へ移す最初の slice を実装する。
- code-behind は UI 型の値を変換し、command を呼ぶだけに近づける。

完了条件:

- `MainWindow.cs` の event handler inventory が `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/` にある。
- 最初の event handler slice が ViewModel command / presentation service へ移っている。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-C1: BMSLibrary source-text helper and facade split foundation`

目的:

- `SourceTextTestHelper.ReadBmsLibrarySourceText()` を追加する。
- `BMSLibrary.cs` 単体配置に依存する source-text / private reflection test を分割耐性のある形へ移す。
- その後、partial split または既存 `BmsLibraryInternal` service への workflow 移動を進められる状態にする。

完了条件:

- source-text test が `BMSLibrary.cs` 単体配置に過度に依存していない。
- `BMSLibrary` facade split の blocker が 1 つ減っている。
- build / test / format / `git diff --check` が通る。
- サブエージェントレビューで重大な指摘がない。

### `REF-MVP-D1: .NET 10 blocker inventory in devdocs`

目的:

- TFM は変更しない。
- `Settings.Default`、`System.Configuration`、`app.config`、HintPath DLL、native DLL、WPF + WinForms、P/Invoke、external process host の blocker inventory を `.tmp` ではなく `devdocs` 管理下に置く。
- .NET 10 本移行ではなく、Refactoring MVP Gate のための blocker 可視化に限定する。

完了条件:

- blocker inventory が git 管理下にある。
- 各 blocker について、該当箇所、影響、refactor 前にできる対処、.NET 10 移行時に検討する対処が書かれている。
- TFM 変更や release 作業をしていない。
- docs のリンクが壊れていない。
- `git diff --check` が通る。

## Guardrail

| 対象 | 現状目安 | Guardrail | 次の extraction 候補 |
|---|---:|---:|---|
| `MainWindowViewModel.cs` | 23,538 行 | 8,000 行以下 | playlist detail build workflow、play history、playback、settings save、library refresh |
| `MainWindow.cs` | 10,289 行 | 5,000 行以下 | `tableContextMenuOpened`、async void 本体、drag/drop、URL download |
| `BMSLibrary.cs` | 21,542 行 | 12,000 行以下 | LR2 sync、package install、maintenance、folder/file operation |
| `BMSPlaylist.cs` | P1 対象 | 6,000 行以下 | P0/P1 境界で再計画 |

Guardrail 超過は現時点では既知。残す理由は「MVP active lanes の extraction 前であるため」。次の extraction 候補は上表を正本とする。

## Blocked / Waiting

| 項目 | 状態 |
|---|---|
| release / version 作業 | Refactoring MVP Gate 通過まで freeze |
| `net10.0-windows` 本移行 | Lane D inventory と Lane A/B/C の責務分離後に dry-run plan を作る |
| 旧 `L-3c-17` | `REF-MVP-A1` の subtask に格下げ。独立 ticket としては扱わない |
| `.tmp` inventories | 継続判断・blocker inventory は `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/` へ移す |

## Latest Completed Work

`REF-MVP-A1` の最初の workflow slice として、`PlaylistDetailBuildWorkflowCoordinator` と `MainWindowViewModel.PlaylistDetailBuildWorkflowHost` を追加し、`TryBuildPlaylistViewAndApply` の decision / dispatch を root から移した。`RebuildPlaylistSource` / `ApplyPlaylistViewWithoutSourceRebuild` の本体はまだ root に残り、次 slice で build/apply/finalize の重複をさらに coordinator へ寄せる。

次にやる 1 件: 標準確認とサブエージェントレビューで重大指摘がなければこの slice を commit し、次 slice で `ApplyPlaylistDetailViewRowsToMainView` の timing result DTO 化または rebuild/view-only finalize 共通化を進める。

## 次回 Codex が最初に読むべきファイル

1. [00_Codex共通実行ルール.md](./00_Codex共通実行ルール.md)
2. [PLAN_STATUS.md](./PLAN_STATUS.md)
3. 推奨最初の実装: [P0-01_MainWindowViewModel_リファクタリング計画.md](./P0-01_MainWindowViewModel_リファクタリング計画.md)
4. [99_調査メモ_現状メトリクス.md](./99_調査メモ_現状メトリクス.md)
