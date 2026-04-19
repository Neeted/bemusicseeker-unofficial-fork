# Phase 3.5: bmson playlist 詳細の整合性 / 性能是正

## 目的

- Phase 3 で入れた `bmson` playlist 詳細反映の整合性不足を解消する
- `bmson owned` 行が未所持経路へ流れることによる単一 playlist reload の重さを解消する
- `NO SONG` 行の title / artist / level 表示崩れを直す

## このフェーズを先に入れる理由

- 現在見えている問題は Pending / 導入先推定ではなく、Phase 3 の責務である「playlist 詳細で既所持として見える」の未完了部分である
- 単一プレイリストの再同期でも重いので、Phase 4 の package / install estimation より前に直すべき
- `bmson owned` と `truly missing` が混在したまま次フェーズへ進むと、Pending 側の所持判定や UI 分岐でも同じ混乱を引きずりやすい

## 現在確認できている課題

- 当初確認できていた課題は、いずれも解消済み
  - `resolvedBmson != null` でも `realFile == null` なら `scoreProbe` を生成していた
  - `resolvedBmson != null` でも clear / rank / status が `NO SONG` 側の扱いになっていた
  - `Title` / `Artist` / `Level` の fallback が `string.Empty` で途中停止していた
  - その結果、Phase 3 の playlist 詳細統合が見た目・処理系の両方で半端になっていた

## スコープ内

- playlist 詳細の `bmson owned` / `truly missing` 判定整理
- `scoreProbe` 生成条件の見直し
- `NO SONG` 行の title / artist / level / folder 表示 fallback 修正
- 単一 playlist reload 後の詳細再構築性能の確認

## スコープ外

- Pending package の `.bmson` 対応
- 導入先推定
- `bmson` 非対応メニュー制御の仕上げ
- `mode_hint` 表示の UI polish

## 実装タスク

1. `BuildPlaylistSourceRows()` の `scoreProbe` 生成条件を `realFile == null && resolvedBmson == null` に絞る
2. `PlaylistDetailSourceRow` の fallback を `??` ではなく `string.IsNullOrWhiteSpace` 基準で組み直す
   - `Title`
   - `Artist`
   - `Level`
   - `Folder`
   - `hash / sha256`
3. `resolvedBmson != null` 行を `NO SONG` と同一扱いにしない
   - unnecessary score probe をしない
   - clear / rank / status の暫定表示を定義する
4. 単一 playlist reload での詳細再構築ログを確認し、`score_probe targetCount` が減ることを確認する
5. 追加ブラッシュアップとして、全体同期の参照差し替え戦略を単体リロードと統一する
6. 追加ブラッシュアップとして、外部プレイリスト更新判定を `playlist_entry active row` と再取得 row の比較へ整理する
7. 追加ブラッシュアップとして、差分調査用の fingerprint ログを追加する

## 設計メモ

- このフェーズは Phase 3 の責務補完であり、Phase 5 の polish ではない
- `bmson` 行のスコア表示や clear / rank を完全にどう見せるかは最終的に Phase 5 で磨ける
- ただし「所持しているのに未所持経路へ入らない」「従来表示されていた entry 情報を失わない」はこのフェーズで必須

## テスト追加方針

- `resolvedBmson != null` かつ `realFile == null` の行で `scoreProbe` を作らないテスト
- `NO SONG` 行でも `entry.title` / `entry.artist` / `entry.level` が維持されるテスト
- `bmson owned` 行が playlist 詳細で owned として表示され、かつ不要な未所持処理に入らないテスト
- 単一 playlist reload 後の詳細反映回帰テスト
- 差分 fingerprint 比較結果の件数 / サンプル数テスト
- 全体同期でも単体リロードと同じ差分ログが出る前提の確認

## 完了条件

- `bmson owned` 行が playlist 詳細で未所持扱いの重い経路に入らない
- `NO SONG` 行でも従来の playlist entry 情報が表示される
- 単一 playlist reload の体感悪化要因が解消している

## 完了状況の整理

- 完了
  - `bmson owned` 行は `scoreProbe` を生成しない
  - `NO SONG` fallback は `title / artist / level / folder / hash / sha256` を維持する
  - `bmson owned` 行は playlist 詳細で owned のまま残る
  - 単体リロードの参照差し替えを限定化した
  - 全体同期も同じ参照差し替え戦略に統一した
  - 外部プレイリスト更新判定を active row 比較へ整理した
  - 差分調査用 fingerprint ログを追加し、全体同期でも出るようにした
- 追加で行ったブラッシュアップ
  - `NUL` を含む比較文字列の canonical 化
  - 外部 JSON に再出現した row に `is_removed = true` を引き継がない調整
  - 全体同期での diff ログ有効化

## Phase 4 への判断

- このフェーズは完了とみなしてよい
- したがって Phase 4 へ進める状態にある
- 理由:
  - Pending / 導入先推定の前提となる playlist 詳細の所持判定と表示責務が安定したため
  - 外部プレイリスト更新判定と差分調査ログも揃い、Phase 4 で判定不具合を追いやすくなったため
