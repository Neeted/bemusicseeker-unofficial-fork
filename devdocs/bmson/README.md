# bmson 対応ロードマップ

本ディレクトリは、`BeMusicSeeker-decomp` に `bmson` 対応を段階的に導入するための作業資料です。  
実装順を固定しつつ、各フェーズを独立して見積もり・着手・レビューできる形に整理しています。

## 目的

- `sha256` を含む外部プレイリスト所持判定を先に成立させる
- LR2 既存テーブル (`song`, `folder`) を壊さずに `bmson` を扱えるようにする
- `bmson` を「再生対応なしの管理対象」として段階導入する
- 途中フェーズでも意味のある成果物が残るようにする

## フェーズ一覧

1. [Phase 1: Playlist SHA-256 基盤](phase-1-playlist-sha256-foundation.md)
   - `playlist_entry.sha256` の追加
   - プレイリストの `md5/sha256` 両対応
   - DataGrid の `sha256` 表示基盤
2. [Phase 1.5: bmson 対応前の移行警告](phase-1-5-migration-warning.md)
   - 初回 migration / backfill 前の警告ダイアログ
   - 過去バージョン互換性に関する注意喚起
   - キャンセル時の安全終了
3. [Phase 2: BMS の md5-sha256 マッピング](phase-2-bms-sha256-mapping.md)
   - 既存 BMS の `sha256` バックフィル
   - `sha256` プレイリストで既存 BMS を所持判定
   - 既存導線へのハッシュ抽象化導入
   - 初回バックフィル進捗表示
4. [Phase 3: bmson カタログ導入](phase-3-bmson-catalog.md)
   - `bmson_song` テーブル追加
   - `bmson` 軽量パース
   - playlist 詳細で `bmson` を所持譜面として解決できるようにする
5. [Phase 3.5: bmson playlist 詳細の整合性 / 性能是正](phase-3-5-bmson-playlist-detail-fix.md)
   - `bmson owned` 行を未所持経路へ流さない
   - `NO SONG` 表示崩れの是正
   - playlist 詳細リロード時の過剰な score probe を抑止
6. [Phase 4: bmson Pending / 導入先推定](phase-4-bmson-pending-install.md)
   - Pending package 検出を `.bmson` 対応
   - `bmson` 差分の導入先推定
   - `bms only` フォルダへの導入制約を扱う
7. [Phase 5: UI / モード / 運用仕上げ](phase-5-ui-mode-and-polish.md)
   - 通常一覧への `bmson` 表示
   - `mode_hint` と `KEYS` / mode filter の整理
   - プレイリストサマリー集計 / 性能と非対応機能の仕上げ
   - 重複ファイルチェックの `bmson` 対応仕上げ
8. [導入先推定精度向上計画](install-estimation-accuracy-improvement-plan.md)
   - 連番リソース系の誤推定対策
   - confidence / 第2候補 / `INSTL DST TITLE/ARTIST`
   - `INSTL DST` 候補サジェスト（オートコンプリート型）
   - low-confidence 行色
   - P2 前提としての scan redesign
     - sibling 廃止
   - chart-directory keyed hash-only scan
   - relative path hash の土台
   - `candidate + package bundled resources` 評価への移行
   - 余剰リソース評価と source folder 扱い見直し
   - metadata tie-break の今後計画
9. [現状の導入先推定ロジック整理](install-estimation-current-logic.md)
   - package-aware / loose-file の 2 経路
   - package snapshot / bundled resources / source candidate surface
   - `candidate + package bundled resources` の最終比較
   - `confidence` / `ShouldAutoApplyDestination` / `INSTL DST` 適用条件
10. [導入先推定のあるべき設計メモ](install-estimation-target-design.md)
   - `candidate + package bundled resources` を採った背景
   - coarse filter と final evaluation の役割分離
   - source folder と threshold の再整理
   - 実装後に残る tuning 論点整理
11. [導入先推定 性能改善の前提整理](install-estimation-performance-foundation.md)
   - 100+ package 一括ドロップ時の性能ホットパス整理
   - coarse filter / fallback 見直しの前提
   - source package surface 列挙基盤と追加キャッシュの論点
   - package 間並列化に入る前の排他 / 適用モデル整理
   - Perf-1: 全件 fallback 廃止と audio 主軸 coarse filter
12. [導入先推定 相対パス対応の前提整理](install-estimation-relative-path-foundation.md)
   - 相対パス譜面を前提にした `bgm1` と `sound\bgm1` の意味整理
   - Everything / fallback / 増分更新の parity 論点
   - broad filter を path-aware にする前のフェーズ分割
   - Phase 1+2 と Phase 3 を実施済み
   - `CountMatches(...)` / viability / confidence を含む Relative Path Phase 4 も実施済み
   - root chart と nested chart directory が共存する package に対する ownership completion も実施済み
13. [chart_info parser compatibility notes](chart-info-parser-compatibility-notes.md)
   - beatoraja / jbms-parser 互換のための BMS / BMSON parser 実装メモ
   - JSON parse, delimiter, Java 型変換, LN, timeline, density, speedchange の注意点
   - `chart_info` の production DB compare / backfill 性能 / 今後の DataGrid 表示論点
14. [chart_info DataGrid display plan](chart-info-datagrid-display-plan.md)
   - `chart_info` 由来カラムの表示仕様
   - 通常一覧 / プレイリスト詳細の LEVEL 整理
   - keyword search field / range 検索の v1 仕様
15. [BMS / bmson 譜面抽象化 移行計画](bms-bmson-chart-abstraction-migration-plan.md)
   - BMS と bmson を共通の「譜面ファイル」として扱うための設計
   - LR2 / BMS 専用操作と共通操作の capability 分離
   - `PendingChartEntry : BMSFile` から段階的に脱却する移行計画
   - Phase D の maintenance 方針: table は共有し、BMS encoding workflow と resource health workflow を分離

## 実装方針の要点

- `song` と `folder` は LR2 互換維持のため変更しない
- `playlist_entry` はアプリ拡張領域として `sha256` を追加する
- `bmson_song` は `path` を主キーにし、`md5` と `sha256` の両方を持てるようにする
- `bmson_song` の `md5` と `sha256` はどちらも非ユニークとする
  - 同一内容の `bmson` が別パスに複数存在し得るため
- 既存 BMS の `sha256` は `song` には持たせず、別マップテーブルで管理する
- `bmson` 専用の再生対応や LR2 依存機能対応はスコープ外とする

## ハッシュ識別子の前提

- 外部プレイリストは `md5` または `sha256` のどちらか片方だけを持つことがある
- 多くの場合は以下の傾向がある
  - BMS 系: `md5`
  - bmson: `sha256`
- ただしこれは絶対ではない
  - BMS 系でも `sha256` のみの可能性がある
  - bmson でも `md5` のみの可能性がある
- したがって、プレイリスト matching はフォーマット固定ではなく `md5/sha256` の両対応前提で設計する
- 優先順位は持つが、フォーマット種別ではなく「その entry が持つ識別子」で判断する
- 既所持確認は BMS 同様に `md5` でもよい
  - `sha256` 対応は主に外部プレイリスト受け入れのために入れる

## 推奨の進め方

- Phase 1 の後に Phase 1.5 を入れる
  - DB 変更と初回バックフィル前の警告導線を先に固める
- Phase 2 で `sha256` プレイリスト所持判定の価値を出す
- Phase 3 で `bmson` の実体管理を追加する
- Phase 3.5 で playlist 詳細の整合性と性能回帰を解消する
- Phase 4 で Pending / 導入先推定へ拡張する
- Phase 5 で UI / 集計 / duplicate を含む運用導線まで仕上げる

## 現在の到達点

- Phase 1, 1.5, 2 は完了
- Phase 3 は完了
  - `bmson_song`
  - `.bmson` 軽量パース
  - playlist 詳細での `md5/sha256` 解決
- Phase 3.5 も完了
  - playlist 詳細の `bmson owned` / `missing` 整理
  - `NO SONG` fallback の維持
  - 単体 / 全体同期の参照差し替え戦略統一
  - 外部プレイリスト更新判定の active row 比較化
  - 差分 fingerprint ログ追加
- Phase 4 も完了
  - Pending / install estimation / regroup の `bmson` 対応
  - `.bmson` 単体ドロップ対応
  - `WARNING / WAV / BGA / MOVIE` 列の `bmson` 対応
  - `md5/sha256` 判定の `key-selection` 化
- Phase 5 も完了
  - 通常一覧への `bmson` 表示
  - `mode_hint` の `KEYS` 表示整理と `TAG` 廃止
  - プレイリストサマリー集計 / 性能是正
  - `bmson` 非対応機能の非表示化
  - 重複ファイルチェックの `BMS + bmson` 対応
- 実装フェーズとしては 1 〜 5 が完了
- 導入先推定精度向上計画は `P1` / `P3` と `P2 前提整備` まで完了
  - confidence / 第2候補 / `INSTL DST TITLE/ARTIST`
  - `INSTL DST` 候補サジェストと low-confidence 行色
  - sibling 廃止と chart-directory keyed scan redesign
- `P2` 本体の評価単位再定義は実装済み
  - package-aware union 評価
  - source の通常候補化
  - threshold の auto-apply 安全弁化
- relative path 対応は ownership fix まで実装済み
  - path-aware broad filter
  - final scoring semantics completion
  - aggregate ownership + self-only ownership の二重 view
- `chart_info` メタデータ基盤は実用状態まで到達
  - `song` テーブルを変更せず、アプリ独自の `chart_info` に譜面メタデータを保存
  - BMS / BMSON の beatoraja / jbms-parser 互換 parser を実装
  - 既存 DB 補完用の起動時 full backfill と、新規追加・install 譜面の inline chart_info 生成に対応
  - production DB compare で non-RANDOM 差分 0 を確認
  - 約 20.9 万譜面の full backfill が timeout 0 で完走

## いま残っているもの

実装フェーズとして大きく未着手のものは多くない。`2026-04-27` 時点で残っているのは、主に次の 5 系統である。

1. DataGrid での `chart_info` 表示設計
   - `chart_info` 由来の `LEVEL`, `DIFFICULTY`, `JUDGE`, `FEATURE`, `TOTAL`, `T/N`, `DENSITY`, `PEAK`, `END`, `LONG`, `SCRATCH`, `SPEEDCHANGE` などの追加
   - `difficulty_defined=false` / `total_defined=false` の警告表示
   - 既存 LR2 `song` 由来値との優先順位
   - keyword search field 拡張
2. 導入先推定の精度 tuning
   - `fingerprint` 比較の導入判断
   - `loose-file merge` と `package merge` の wrapper 差整理
   - 実機ケースでの `raw precision / jaccard` 重み調整
3. 導入先推定の性能 tuning
   - source-side 列挙の regress は解消済み
   - 残差は `pending estimate` の評価 / orchestration 側
4. install / merge 実処理側の列挙再設計
   - `BmsLibraryPackageInstallService` の install / merge package discovery と実処理列挙
   - これは current Perf-3 スコープ外として残している
5. リリース整理
   - README / リリースノート / バージョン反映

## どの資料を見ればよいか

資料の役割は次の 4 種類に分けて読むと分かりやすい。

### 1. 現在の実装状態を知る資料

- [現状の導入先推定ロジック整理](install-estimation-current-logic.md)
  - 現在の code path / snapshot / confidence / diagnostics の説明
- [導入先推定 性能改善の前提整理](install-estimation-performance-foundation.md)
  - 現在の perf 論点と、source-side enumeration regress が解消済みであることの整理
- [導入先推定 相対パス対応の前提整理](install-estimation-relative-path-foundation.md)
  - relative-path 対応の完成形と、その後どこまで cleanup 済みかの整理

### 2. 現在の target / tuning 論点を見る資料

- [導入先推定精度向上計画](install-estimation-accuracy-improvement-plan.md)
  - まだ残っている精度 tuning の論点
- [導入先推定のあるべき設計メモ](install-estimation-target-design.md)
  - 実装後も残る設計上の tuning 論点

### 3. 履歴として残している資料

- [library-scan-native-aggregation-plan.md](library-scan-native-aggregation-plan.md)
  - library build / source-side enumeration regress をどう解消したかの履歴
  - 現在は historical record としての性格が強い

### 4. bmson 導入フェーズの履歴

- `phase-1` 〜 `phase-5`
  - いずれも完了済み
  - 新しく着手するための plan というより、何をどの順で入れたかの履歴として読む

## ひとことで言うと

- bmson 対応フェーズ 1〜5 は完了
- relative-path 対応と source-side enumeration regress 解消も完了
- chart_info メタデータ基盤も production DB compare 差分 0 / timeout 0 まで完了
- いま残っている主論点は
  - chart_info を DataGrid / keyword search へどう見せるか
  - BMS / bmson を共通の譜面抽象で扱い、BMS 専用処理の誤適用をなくすこと
  - bmson resource health を shared `maintenance` table に載せつつ、encoding / 文字化け workflow から分離すること
  - 精度 tuning
  - pending estimate の評価 / orchestration 側 perf
  - install / merge 実処理列挙
  - リリース整理
  の 5 つである

## バージョン方針

- `bmson` 対応完了後は `v2.0.0.0` へのメジャーバージョンアップを想定する
- ただし、全フェーズ完了まではバージョン番号自体は変更しない
- したがって、各フェーズ資料には version up 作業を含めない
- README / リリースノート / バージョン反映は Phase 5 完了後の別作業として扱う

## 関連資料

- [../spec/workflows.md](../spec/workflows.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
- [../spec/TECH_SPEC.ja.md](../spec/TECH_SPEC.ja.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
