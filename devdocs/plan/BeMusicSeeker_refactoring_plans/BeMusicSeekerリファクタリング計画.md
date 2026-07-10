# BeMusicSeeker リファクタリング完了計画

この文書は、リファクタリングの目的、完了 Gate、順序付き outcome backlog の正本である。
現在の作業位置は [PLAN_STATUS](./PLAN_STATUS.md)、実行方法は [Codex 共通実行ルール](./00_Codex共通実行ルール.md)、移行阻害要因は [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) を参照する。

## 目的

次を完了し、その後の課題を純粋な `.NET 10` / `net10.0-windows` 移行として扱える状態にする。

- `MainWindow` と `MainWindowViewModel` を MVVM の shell / composition / view-host 境界へ縮小する。
- 巨大型に集中した application workflow、domain workflow、状態所有を、機能単位の owner へ移す。
- `BMSLibrary` と `BMSPlaylist` を public compatibility facade / aggregate entry へ縮小する。
- settings、DB、native interop、外部 process、WPF / WinForms、出力 layout を明示的な adapter / gateway 境界へ閉じる。
- 既存の UI observable behavior、DB schema、setting key、serialized value、外部ファイル形式を、別途承認された変更なしに変えない。

## Release Freeze と権限

Refactoring Completion Gate を満たすまでリリース作業を凍結する。Gate 通過はリリースの自動許可ではない。

Codex は、各 implementation unit の検証とサブエージェント静的レビュー後に、自発的に commit してよい。一方、次はユーザーの明示指示があるまで禁止する。

- `git push`、Git tag、GitHub Release、Release draft。
- version、release notes、publish / release script のリリース目的変更。
- 配布 package の作成と公開。
- Gate 通過を理由にした自動的なリリース準備。

build / test のローカル artifact と、移行 blocker を解消するための release 関連コードの refactor は許可する。

## 計画単位

### Outcome

Gate を一段進める、利用者またはアーキテクチャから見て完了を判定できる責務移管。複数 commit で構成してよい。

### Implementation unit

独立して検証・静的レビュー・commit できる変更単位。同じ outcome ID を使い、完了まで連続して進める。

### Subtask

DTO、result、helper、rename、test fixture、partial split、signature 調整など。これらだけを outcome、ticket、checkpoint、decision record にしない。

次は禁止する。

- production code を変えず「次の 1 seam」を選ぶ checkpoint。
- private helper や reflection test の残数を起点にした無期限の ticket 生成。
- 旧 owner / 旧経路を残したまま coordinator、host、adapter だけを成果とすること。
- Git 履歴で確認できる完了作業、テスト件数、行数推移を計画資料へ追記すること。

## 目標アーキテクチャ

### UI shell

`MainWindow.xaml` / `MainWindow.cs` に残せる責務:

- Window lifecycle、focus、selection、scroll、hit test。
- WPF / WinForms host、OS window handle、View 固有型を扱う薄い adapter。
- command / request へ即時委譲する event entry point。
- feature View / UserControl の composition。

`MainWindowViewModel` に残せる責務:

- application composition と lifecycle。
- child ViewModel / application service の所有。
- dialog request、progress、UI thread の境界。
- feature 間をまたぐ最小限の navigation / coordination。

main chart list、playback、playlist workspace、play history、settings、library refresh、package operation の state と workflow は feature owner が持つ。root の binding relay、PropertyChanged 再中継、private workflow host は完了後に削除する。

main table 周辺は、次の三つの ownership boundary を混ぜない。

- **Main table presentation owner**: 現在 rows、column presentation、selection、table sort interaction、atomic apply、旧 rows の dispose、PropertyChanged と適用完了通知を持つ。
- **Mode workflow owner**: request identity、source / query / filter / sort の意味、cache、cancellation、freshness、presentation result の生成を mode ごとに持つ。regular chart は UI-01、playlist workspace と play history の workflow はそれぞれ UI-03 / UI-04 の owner が最終的に持つ。
- **Shell / composition**: 現在 mode の選択、mode owner と table presentation owner の接続、Window 固有 event の request 変換、feature 間の最小限の coordination だけを持つ。

mode workflow owner は immutable な presentation result を生成し、table presentation owner が terminal apply を atomic に行う。UI-01 は playlist / play-history workflow 全体を MainChartList へ吸収せず、現 owner から result を受ける production contract と table 側の適用責務までを閉じる。

### Domain

`BMSLibrary` は public compatibility API、event surface、依存の composition、複数 owner をまたぐ最小限の coordination に限定する。initialization / scan、package install、file operation、maintenance / resource health、LR2 song.db sync、playlist reference は、それぞれ状態と挙動を同じ owner が持つ。

`BMSPlaylist` は public compatibility API と playlist aggregate entry に限定する。persistence / reload、external sync、custom-folder output、BMT export、recommended-table build、operation notification は独立して検証できる owner へ移す。

新しい owner は `Window`、`Control`、`MessageBox`、`Settings.Default`、`NLog`、無制御な DB connection / transaction、無制御な `Dispatcher` を直接参照しない。必要な能力は用途別の小さな contract として受け取る。

active outcome の成立に不可欠な platform / composition contract は、production 経路へ即座に接続する最小単位に限り後続 outcome から前倒ししてよい。未使用 interface、総合 facade、将来用の state 保存は追加しない。

### Configuration と platform boundary

settings の load / edit / save / upgrade、application base directory、managed / native dependency layout、P/Invoke、audio、external process、updater、WPF / WinForms host は明示した adapter / gateway が所有する。application / domain workflow は snapshot または用途別 interface を受け取る。

## Refactoring Completion Gate

次をすべて満たしたときだけ Gate を通過できる。

1. UI ownership
   - 各 feature View が child ViewModel を直接 DataContext とし、root binding relay が残っていない。
   - code-behind の `async void` は event entry point に限られ、処理本体を持たない。
   - root ViewModel は上記の許可責務だけで説明できる。
2. Library ownership
   - 列挙した library workflow が明示的 owner を持ち、state と behavior が同じ境界にある。
   - `BMSLibrary` の private state を写すだけの巨大 host interface や、test 専用 production seam が残っていない。
3. Playlist ownership
   - persistence / reload と external sync / output が `BMSPlaylist` から分離され、単体で検証できる。
4. Configuration / platform ownership
   - `Settings.Default`、DB、native、external process、UI technology への直接依存が、許可した adapter / gateway に限定される。
5. Migration readiness
   - [.NET 10 migration blocker register](./DOTNET10_MIGRATION_BLOCKERS.md) の各未解決項目が、project / package / runtime / adapter の移行作業として説明できる。
   - blocker 解消のために巨大型内部の workflow 分割や MVVM 再設計を必要としない。
6. Quality
   - build、test、format / whitespace、`git diff --check` が通る。analyzer が正常終了し、今回差分による warning が増えていない。
   - outcome 全体のサブエージェント静的レビューで重大な指摘がない。

## Size guardrail

行数は唯一の完成判定ではないが、巨大コード整理の外側 guardrail とする。

| 対象 | 2026-07-10 baseline | Gate guardrail |
|---|---:|---:|
| `MainWindowViewModel.cs` root | 23,413 | 8,000 以下 |
| `MainWindow.cs` | 10,327 | 5,000 以下 |
| top-level `BMSLibrary*.cs` family | 20,157 | 12,000 以下 |
| top-level `BMSPlaylist*.cs` family | 10,273 | 6,000 以下 |

partial split、nested type 移動、host file 追加で数値を達成したことにはしない。logical owner family、残る責務、依存方向、最大 workflow、testability を併せて確認する。baseline 値は履歴として更新せず、Gate audit 時は実ソースから再計測する。

## Ordered outcome backlog

Codex は [PLAN_STATUS](./PLAN_STATUS.md) の active outcome を進める。active outcome が未指定なら、次の順で最初の未完・非 blocked outcome を選ぶ。将来 outcome の class / interface 名は着手時まで固定しない。

| Order | Outcome | Exit condition |
|---:|---|---|
| 1 | `UI-01 Main table presentation and regular chart ownership` | main table presentation と regular chart workflow の owner が閉じ、playlist / play-history owner からの result contract が production 接続され、XAML の child binding と root relay / callback host 削除が完了する |
| 2 | `APP-01 Composition and configuration ownership` | startup と settings load / edit / save が明示的 owner を通り、後続 feature / service が global settings を直接取得せず構築される |
| 3 | `UI-02 Playback ownership` | playback state / command / progress が child View / ViewModel と player adapter に移り、code-behind は view-host 操作だけになる |
| 4 | `UI-03 Playlist workspace ownership` | tree、source query / cache / cancellation、detail / summary result generation、feature-local interaction、drag-drop、reload / edit workflow が workspace owner に移り、result の main-table terminal apply を除く root / code-behind の playlist workflow がなくなる |
| 5 | `UI-04 Play history ownership` | source query / cache / cancellation、filter、result generation、selected row の feature-local action interpretation が feature owner に移り、main-table selection state / terminal apply を除く root relay と UI workflow がなくなる |
| 6 | `LIB-01 Initialization and scan ownership` | startup scan、file diff、parse、commit、post-scan maintenance が明示的 pipeline owner に移り、facade は request / lifecycle bridge になる |
| 7 | `LIB-02 Package and file-operation ownership` | package install / repair / merge / rename / delete が state owner と用途別 gateway を通り、temporary host 群と重複 orchestration が整理される |
| 8 | `LIB-03 Maintenance and resource-health ownership` | hydration、mutation、dispatch、index rebuild の状態と挙動が一つの bounded owner に集約され、C65-C103 由来の bridge / test seam が整理される |
| 9 | `LIB-04 LR2 sync and playlist-reference ownership` | input build、schedule、run、publish と playlist reference update が facade private state から独立する |
| 10 | `PL-01 Playlist persistence and reload ownership` | DB transaction、hydration、diff、reload decision が repository / workflow owner に移り、互換 facade から分離される |
| 11 | `PL-02 Playlist external-sync and output ownership` | HTTP sync、custom-folder、BMT、recommended-table output が個別に検証できる owner へ移る |
| 12 | `UI-05 Shell closure` | settings dialog、library refresh、package operation、残存 feature の root pass-through と code-behind workflow がなくなり、UI Gate を満たす |
| 13 | `MIG-01 Platform boundary closure` | settings/config、native load、managed/native output、external process、updater、WPF / WinForms の残依存が migration adapter / project task に限定される |
| 14 | `GATE-01 Refactoring completion audit` | 全 Gate evidence、実測値、全体検証、静的レビューを確認し、残作業を `.NET 10` migration plan に引き渡せる |

依存により active outcome が真に blocked の場合だけ、理由を `PLAN_STATUS.md` に 1 行で記録して次の非 blocked outcome へ進む。単に難しい、調査が必要、変更量が大きいことは blocked 理由にしない。

## Outcome completion rule

各 outcome は次を満たすまで完了にしない。

- state と behavior の新 owner が明確である。
- production の通常経路が新 owner を使う。
- 旧 owner、旧 binding、旧 callback / host、不要な compatibility path を削除している。
- private 実装配置ではなく behavior を検証する test がある。
- outcome 開始時より root の責務または migration blocker が測定可能に減っている。
- outcome 全体の検証と静的レビューが完了している。

implementation unit は seam や private helper の個数ではなく、1 つの user-visible workflow または ownership boundary を通常経路から旧経路削除まで閉じる大きさにする。新 owner への委譲だけを追加して旧 owner を残す commit、調査結果だけの commit、完了証跡だけの docs commit は作らない。

UI outcome の完了時は、移管済み child owner から `Application.Current`、`DispatcherHelper.UIDispatcher`、`MainWindowViewModel` の nested contract、`*ForTest` production method、root PropertyChanged relay、root callback / workflow host がなくなっていることを検索と静的レビューで確認する。例外が必要なら暗黙に残さず、当該 outcome の acceptance criteria で許可境界として説明する。

完了結果の履歴は Git commit に残し、計画資料には outcome の状態だけを記録する。

## Gate 後

Gate 通過後は `.NET 10` 移行を別計画として開始する。TFM、package、runtime layout、user settings migration、native deployment、updater 方針をそこで決定する。リリース作業は、Gate 通過後もユーザーの明示指示なしには開始しない。
