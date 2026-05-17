# 導入先推定精度向上計画

## 完了状況

- `P1`: 実施済み
- `P3`: 実施済み
- `P2 前提整備（scan redesign）`: 実施済み
- `P2 本体（評価単位の再定義と余剰リソース評価）`: 実施済み
- `P2 最終調整（通常推定とマージ推定の意味分離）`: 実施済み
- `P4`: 実施済み
- `Relative Path Phase 4`: 実施済み
- `Perf-1`: 実施済み
- `Perf-2a`: 実施済み
- `Perf-2b`: 実施済み
- `P5`: 実施済み

## 現在の整理

`2026-04-23` 時点の推定は、次のように整理されています。

- 通常の `インストール先を推定`
  - `candidate + package bundled resources`
  - source excluded
  - external install destination search
- `マージ先を推定`
  - `candidate only`
  - source excluded
  - external standalone merge search
- background auto-estimate 抑制
  - source baseline health を使う
  - `DeferredEstimateReason=HealthySourceBaseline`

つまり現在は、

- source baseline は **自動推定を始めるかどうか**
- ranking 本体は **external candidate をどう並べるか**

で役割を分けています。

## Relative Path / Perf 整理

`2026-04-23` 時点では、relative path 対応に伴う性能対策もひとまず完了扱いでよい。

- 最新 batch は `elapsedMs=29672`
- `Pref-2b` の `28628` に対して `+1044ms (+3.6%)`
- ownership perf recovery 第1段階後の `31829` からは `-2157ms (-6.8%)`
- `evaluationMs` 合計は `706` で、`Pref-2b` の `1605` より軽い

このため、relative path 対応による致命的な性能回帰はない整理とし、次段は `Phase 6. Cleanup / Legacy Removal / Perf-3 接続` を進める。

この次段は **Estimation First** スコープで進める。

- library build 側と package source surface 側で shared root enumeration backend を使う
- Everything 経路は bridge-only とし、managed 側から `Everything3_x64.dll` を直接使わない
- `ChartPackage` に package source scan snapshot を持たせ、`BMSFiles` と install-estimation surface を再利用する
- mixed package は installed-dir resolve 基準の用語へ揃え、legacy search 命名は使わない
- `BmsScanResult` の obsolete compat 面と未使用 `DirectoryResourceIndex` を cleanup する
- package surface metrics / logging を追加し、source-side wall-clock を可視化する

一方で、このフェーズでは `BmsLibraryPackageInstallService` の install/merge package discovery 列挙は扱わない。

## P2 で実装した本質

### 1. package-aware union 評価

通常推定では final evaluation を

- `candidate + package bundled resources`

に変更しました。  
これにより、追加音源つき差分で destination 側が過小評価される問題を緩和しています。

### 2. merge の `candidate only` 化

`マージ先を推定` は、

- source の同梱リソースを使わず
- external candidate 単体で成立する統合先があるか

を見る機能に整理しました。

### 3. source baseline の役割縮小

source は ranking 本体から外し、現在は

- high-health pending の background auto-estimate 抑制

にだけ使います。

## 現在の confidence 方針

confidence は external candidate だけで決めます。

- `High + destination`
  - viable external candidate が 1 位で明確
- `Low + suggestions`
  - viable external candidate 同士が僅差
- `High + no destination`
  - viable external candidate がない

source 前提の confidence reason は現在の主経路では使いません。

## UI 反映

- `INSTL DST TITLE` / `INSTL DST ARTIST` は、`INSTL DST` が UI に反映される経路では必ず同期する
- low-confidence 行だけ warning / 候補 suggestion を保持する
- high-health deferred package は
  - `INSTL DST = null`
  - suggestion なし
  - warning なし
  のまま pending に残す

## Relative Path Phase 4 で追加したこと

- broad filter で分けた basename-only / path-aware semantics を final evaluation まで一貫化
- basename-only ref
  - basename 一致で `1` match
- path-aware ref
  - relative path 完全一致でのみ `1` match
  - basename-only matchにはフォールバックしない
- `Defined` / `Matched` / `CandidateCount`
  - basename-only + path-aware を合算した total resource count を基準化
- `*ExactMatched`
  - 互換のため残すが独立 bonus 軸からは外し、`*Matched` と同値に整理
- viability / audio gate も同じ per-ref semantics に更新

これにより、相対パス譜面は

- basename-only 誤候補へ行かない
- 正しい path-aware candidate がある場合は拾える
- まだ成立しない candidate は normal / merge の mode semantics に従って `no destination` または `Low` に留まる

という期待値に寄せた。

## Perf-2b で追加したこと

- background `pending_estimate_batch` を `evaluate parallel / apply serial` に再編
- package 間だけ bounded parallel にし、package 内 candidate 評価は background path で degree `1`
- package-level の `estimate_install ...` ログと `completed=x/y` progress は request / apply 順を維持
- manual estimate の優先順位や精度ロジック自体は変更しない

つまり Perf-2b は、**精度ロジック不変のまま background 実行モデルだけを変更した改善**として扱う。

## 今後の残課題

- fingerprint 系比較
- loose-file merge と package merge の wrapper 差整理
- 実機ケースでの raw precision / jaccard 重み調整

## P4 で実装したこと

- metadata は全候補ではなく **上位 frontier** にだけ使う
- candidate 側は directory 配下の **最頻値 title/artist/pair**
- target 側も package / loose-file 単位の **最頻値 profile**
- v1 の一致方式は **正規化後完全一致**
- metadata は主スコア化せず、tie-break と `Low -> High` の最終判断補助に限定

現在の `ConfidenceReason = metadata_tiebreak_distinct` は、

- resource 指標では viable candidate が僅差
- ただし metadata では 1 位候補だけが明確に一致

というケースを表します。

## P4b で追加したこと

- metadata を **selected-candidate validation** にも使う
- `TITLE` は exact に加えて **軽量 fuzzy**
- `ARTIST` は先頭 prefix 除去ではなく、**文字列中の差分作者 suffix 切り落とし**
- viable candidate が 1 件だけでも metadata が弱ければ `Low`
- metadata mismatch の場合は
  - `INSTL DST` を空にする
  - suggestion に top candidate を 1 件残す
  - ambiguity とは別 warning を出す

## Perf-1 で追加したこと

- `candidateDirsAfter=0` の全件 fallback を廃止
- coarse filter を broad prefilter + audio gate の二段へ整理
- `audioRefs > 0` の譜面では audio 一致を candidate 成立の最低条件に変更
- `audioRefs >= 2` は 2 件一致、`audioRefs == 1` は 1 件一致を必須化
- `audioRefs == 0` の譜面だけは audio gate を適用しない

## Perf-2a で再整理したこと

### 1. `innerWavHealthThreshold` を viability gate として前段へ寄せる

Perf-2a 修正後は、`innerWavHealthThreshold` を含む前段条件を **1 本の unified audio gate** にまとめた。

- `candidate self minimum match`
  - 前段の candidate 縮小
- `innerWavHealthThreshold=70`
  - mode-aware な effective audio viability 判定
  - 最終 viable 判定

に分かれている。

ただしこのままだと、

- 最終的な audio health が `70` 未満
- それでも `Low` 候補や suggestion として残る

ケースがあり、精度面でも UI 面でも価値が薄い。

つまり現在は、`innerWavHealthThreshold` を **より手前の viability gate** としても使っている。

### 2. mode ごとの適用単位

この viability gate は、現在の mode 意味を維持したまま入れている。

- 通常推定
  - `candidate self minimum match`
  - かつ `candidate + package bundled resources` の effective audio health が `70` 超
- マージ推定
  - `candidate self minimum match`
  - かつ `candidate only` の effective audio health が `70` 超

つまり、

- 通常推定
  - 「移動後に成立するか」
- merge 推定
  - 「宛先単体で成立するか」

の違いを保ったまま、**成立しない candidate は suggestion に残さない**方向へ寄せる。

### 3. 精度面での狙い

この再整理の狙いは、単なる性能改善ではなく次でもある。

- `innerWavHealthThreshold` 未満の candidate を warning/suggestion に残さない
- bundled だけで threshold を満たしても、candidate 自身に音源根拠がない宛先は残さない
- 宛先として有効なのは
  - 「移動後に audio health が threshold 以上」
  - かつ
  - 「その中でも余分な音源が少ない相応しい resource 集合」
  という意味を明確にする

音源以外の resource は引き続き評価に使うが、候補絞り込みと rank の主軸は今後も audio に置く。

### 4. 実装時の注意点

- `audioRefs == 0` の特殊譜面は、Perf-1 同様に別扱いが必要
- unified audio gate は 1 helper だが、
  - `candidate self minimum match`
  - mode-aware effective viability
  の 2 条件を持つ
- metadata tie-break / metadata validation は、threshold を超えた candidate 群に対してだけ意味を持つ

つまり Perf-2a の本質は、

- 「候補サジェスト対象になるには、最終的に成立している必要がある」
- かつ「candidate 自身にも最低限の音源根拠が必要である」

という条件を前段へ寄せたことにある。

Perf-2a 再修正では、この意味自体は変えず、

- self minimum match を early-exit 化
- viability 判定を threshold 到達 boolean 化

することで coarse filter CPU を減らした。  
つまりここでの変更は、精度ロジックではなく**実装の軽量化**である。

## 関連資料

- [install-estimation-current-logic.md](../../spec/install-estimation-current-logic.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
- [install-estimation-performance-foundation.md](install-estimation-performance-foundation.md)
