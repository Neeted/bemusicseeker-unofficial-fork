# Phase 5: UI / モード / 運用仕上げ

## 目的

- `bmson` が playlist / Pending だけでなく通常一覧画面にも自然に混在できるようにする
- `mode_hint` を既存 UI の `KEYS` / `ModeFilterType` に整合する形で見せる
- 非対応機能を明示的に隠し、運用上の誤解や誤操作を減らす
- プレイリストサマリー集計を、現在の `md5/sha256` 設計に合わせて整理する
- 重複ファイルチェックを `BMS + bmson` 前提で運用できる状態まで仕上げる

## このフェーズで得たい成果

- 通常一覧画面にも `bmson` 行が表示される
- `bmson` が存在するフォルダでも「フォルダの自動リネーム」が機能する
- プレイリストサマリーの `TOTAL` / `OWNED` が現設計に沿って安定する
- プレイリストサマリーの build / sort / filter が実用的な速度で動作する
- `mode_hint` が `KEYS` 列と mode filter に整合して表示される
- `bmson` 非対応機能が UI から見えない、または結果として出てこない
- 重複ファイルチェックで `bmson` を含む重複グループの表示・解消・再集計が成立する

## 現時点の達成状況

- 完了
  - 通常一覧画面に `bmson` 行を混在表示できる
  - `mode_hint` の `TAG` 列表示を廃止し、`KEYS` 列へ寄せた
  - `beat-* / popn-* / keyboard-24k / keyboard-24k-double` の `KEYS` 表示整理が入っている
  - `bmson` 非対応メニューは非表示化され、ゼロノート / 未登録 / 文字化け画面には `bmson` 行が出ない
  - `bmson` 混在フォルダでも「フォルダの自動リネーム」が機能する
  - `bmson` の FOLDER 編集 / rename 後反映は BMS と同等の inline update 寄りに整理済み
  - プレイリストサマリーの `TOTAL / OWNED / MISSING` は Phase 5 の仕様どおりに集計される
  - プレイリストサマリーは raw build と presentation を分離し、sort / filter で full rebuild しない
  - 重複ファイルチェックは `BMS + bmson` 共通 snapshot で解析される
  - 重複画面の単一フォルダ cleanup / 複数フォルダ merge / snapshot 再生成まで `bmson` 対応済み
- Phase 5 完了後の別作業
  - README / リリースノート反映
  - `release notes\\v2.0.0 リリースノート.md` などへの記述

## スコープ内

- 通常一覧画面への `bmson` 表示
- `mode_hint` と `KEYS` / `ModeFilterType` の整理
- プレイリストサマリー集計の見直し
- プレイリストサマリーの性能是正
- `bmson` 非対応メニューの非表示化
- `bmson` を各種一覧・運用画面へ混ぜたときの UI 整合性調整
- 重複ファイルチェックの `bmson` 対応

## スコープ外

- `.bmson` 再生
- LR2 IR 連携の `bmson` 対応
- `md5/sha256` のどちらで解決したかの可視化
- README / リリースノート更新
  - `release notes\\v2.0.0 リリースノート.md` などへの反映は Phase 5 完了後に別作業で行う

## 主な対象コード

- `BeMusicSeeker/Views/MainWindow.xaml`
- `BeMusicSeeker/Views/MainWindow.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`
- `BeMusicSeeker/ViewModels/GridRowResolver.cs`
- `BeMusicSeeker/Models/BMSLibrary.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDuplicateService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs`
- `BeMusicSeeker/Properties/Resources.*`
- `lang/*.json`

## 実装済みタスク整理

1. 通常一覧画面に `bmson` 行を表示する
   - playlist / Pending 専用ではなく、通常のメイン一覧にも `bmson` を含めた
   - BMS と `bmson` が混在しても既存のソート・絞り込み・右クリック導線が破綻しない形に整理した
2. `bmson` が存在するフォルダでも「フォルダの自動リネーム」が動くようにする
   - BMS だけを前提にした対象抽出や folder 判定を広げた
   - `bmson` 混在フォルダでも rename 候補生成と rename 実行が成立するようにした
   - `bmson` の FOLDER 編集 / rename 後反映も BMS と同様の UX へ寄せた
3. プレイリストサマリー集計を整理する
   - `TOTAL` は `md5` または `sha256` のいずれかが定義されている `is_removed = false` の active entry 数にした
   - `md5/sha256` のどちらも無い行は `TOTAL` に含めない
   - `OWNED` は playlist entry が `md5` を持つなら `md5` で判定し、`md5` が無く `sha256` のみのときだけ `sha256` で判定する
   - `md5` と `sha256` の両方がある row で、`md5` 不一致時に `sha256` まで見に行かないようにした
   - raw build と presentation を分離し、sort / keyword / owned filter では full rebuild しないようにした
4. `mode_hint` と `KEYS` / mode filter の対応を整理する
   - `TAG` 列への `mode_hint` 表示を廃止した
   - `KEYS` 列や mode filter で次を扱うようにした
     - `beat-5k` => `5 KEYS`
     - `beat-7k` => `7 KEYS`
     - `beat-10k` => `10 KEYS`
     - `beat-14k` => `14 KEYS`
     - `popn-5k` => `9 KEYS`
     - `popn-9k` => `9 KEYS`
     - `keyboard-24k` => 表示専用 `24 KEYS`
     - `keyboard-24k-double` => 表示専用 `48 KEYS`
     - その他 => `? KEYS`
   - `ModeFilterType` として利用可能なのは `5/7/9/10/14` のまま維持した
   - `24k/48k` は表示専用で、既存 filter には無理に押し込まない
5. 非対応機能を `bmson` 行で非表示化する
   - コンテキストメニューでは `bmson` 非対応機能を非表示にした
   - ゼロノート検索画面、LR2 データベース未登録画面、文字化けチェック画面には `bmson` 行が出ない構成にした
6. 重複ファイルチェックを `BMS + bmson` 共通化した
   - 重複解析を `BMS + bmson` の共通 snapshot ベースへ拡張した
   - 重複画面の単一フォルダ cleanup が `bmson` を含んでも成立するようにした
   - 複数フォルダ重複グループの merge も `BMS + bmson` 共通 merge に広げた
   - duplicate snapshot は `BMSFiles` / `BmsonSongs` 変更で invalidate し、重複画面アクティブ時は自動再生成されるようにした

## 現時点の前提

- Phase 4 までで `bmson` の playlist / Pending / install estimation は成立している
- `md5/sha256` の解決は Phase 4 ブラッシュアップで `key-selection` に整理済み
- したがって Phase 5 では、ハッシュ解決ロジック自体の追加ではなく UI 表示と集計規則の整理に集中できる
- 追加で、duplicate まわりの運用導線も `BMS + bmson` 前提で揃える必要があると判明したため、Phase 5 内で吸収した

## 非対応機能の扱い

以下は `bmson` では非表示または一覧に出さない扱いが妥当。

- LR2 データベース未登録
- ゼロノート検索
- 文字化けチェック
- LR2IR のスコア照合系カラム
- LR2 IR を開く
- 譜面ビューアで開く
- ランキングデータの更新
- 音声ファイルに変換

## 設計メモ

- `TAG` 列は補助メタデータ列であり、`mode_hint` 表示場所として使い続けない
- `mode_hint` は `KEYS` 表示と mode filter の整合を優先して整理する
- `popn-5k => 9 KEYS` は表現として不自然でも、PMS と同じ `9 KEYS` フィルタへ寄せられる方を優先する
- プレイリストサマリーは、現在の `key-selection` 方針と完全に揃える
- プレイリストサマリーの性能問題は `sha256` 自体ではなく build 構造の問題なので、raw build / presentation 分離で吸収する
- `md5/sha256` のどちらで解決したかは UI に出さない
  - 推定時の既所持判定は `md5` 主体へ整理済み
  - プレイリスト所持判定も「row が持つ主キーで一度だけ判定」の設計が明示済み
- 重複ファイルチェックは、重複ディレクトリ配下の全譜面を group に含める既存仕様を維持する
- 重複判定と duplicate merge / cleanup / reinstall skip のキーは、Phase 4 の key-selection に揃える

## テスト追加方針

- 通常一覧画面に `bmson` 行が出るテスト
- `bmson` 混在フォルダで「フォルダの自動リネーム」が動くテスト
- プレイリストサマリーの `TOTAL` / `OWNED` 集計テスト
  - `is_removed = true` 行を除外する
  - `md5/sha256` の無い row を `TOTAL` に含めない
  - `md5` がある row は `md5` だけで `OWNED` 判定する
  - `sha256 only` row は `sha256` で `OWNED` 判定する
- `mode_hint -> KEYS` 変換テスト
- `popn-5k / popn-9k / 24k / 48k / unknown` の表示確認テスト
- `bmson` 行でコンテキストメニュー非対応項目が非表示になるテスト
- ゼロノート検索、LR2 データベース未登録、文字化けチェックに `bmson` 行が出ないことのテスト
- プレイリストサマリーの sort / filter で full rebuild しないことのテスト
- 重複ファイルチェックで `bmson only` / `BMS + bmson` 混在 group が解析されるテスト
- `bmson` の duplicate cleanup / duplicate merge / snapshot 再生成の回帰テスト

## 完了条件

- 通常一覧画面にも `bmson` 行が表示される
- `bmson` 混在フォルダでも「フォルダの自動リネーム」が機能する
- プレイリストサマリーの `TOTAL` / `OWNED` が現設計どおりに集計される
- プレイリストサマリーの初回 build と sort / filter が実用速度で動作する
- `mode_hint` が `KEYS` 列と mode filter に整合して扱われる
- `TAG` 列への `mode_hint` 表示が廃止されている
- `bmson` 非対応機能が UI 上で誤って使えない
- 重複ファイルチェックで `bmson` を含む group の表示・cleanup・merge・snapshot 再生成が成立する
- README / リリースノート更新が Phase 5 完了後の別作業であることが明確

## リスク

- 通常一覧へ `bmson` を混ぜることで、既存 BMS 一覧前提のフィルタや操作が漏れて露出する可能性
- `KEYS` 表示と `ModeFilterType` の責務を混ぜると、24k/48k を filter 可能と誤解させる可能性
- プレイリストサマリー集計の見直しで、従来 `TOTAL` に含まれていた識別子なし row が減ることによる表示変化
- duplicate group payload は既存どおり重複ディレクトリ配下の全譜面を持つため、将来的な件数増加では別途性能監視が必要
