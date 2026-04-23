# 導入先推定のあるべき設計メモ

## 目的

この資料は、`P2. 余分リソースの少なさを評価へ入れる` を進める中で整理した、**推定機能の意味付け**のメモです。  
`2026-04-22` 時点では、ここで整理した大枠の設計を実装へ反映しています。

## 実装状況メモ

現在は次の整理を採用しています。

- 通常の `インストール先を推定`
  - **`candidate + package bundled resources`**
  - source excluded
  - external install destination search
- `マージ先を推定`
  - **`candidate only`**
  - source excluded
  - external standalone merge search
- source baseline は
  - background auto-estimate 抑制
  - `DeferredEstimateReason=HealthySourceBaseline`
  の判定にだけ使う
- `Fix` 相当の内部モードは `ReinstallCorrection` に整理し、既存ライブラリ譜面の「再インストール先を推定」専用に限定する
  - source bundled resources は使わず、譜面単体を候補へ置いた場合の `candidate only` 評価で判定する
  - 現在配置を baseline とし、候補が baseline より改善する一意候補の場合だけ自動適用する

## 設計の主旨

以前のズレは、最終評価が `candidate only` に寄っていたことと、source を ranking 本体へ混ぜていたことにありました。  
その結果、

- package を足せば成立する destination が過小評価される
- source が無情報でも特別扱いで勝てる
- merge の意味が「source より良いか」と「source 以外で成立するか」の間でぶれる

という問題が出ていました。

これを解くため、現在は **評価単位** と **source の役割** を分けています。

## あるべき役割分担

### 通常推定

通常の `インストール先を推定` は、

- 「この package を外部のどこへ入れれば成立するか」

を見る機能です。  
したがって final evaluation は、

- `candidate + package bundled resources`

で行います。

このとき source は候補に入れません。  
source を候補に戻すと、「source が成立しているか」と「外部 destination があるか」が混ざるためです。

### マージ推定

`マージ先を推定` は、

- 「source の同梱リソースを使わず、source 以外に既存リソースだけで成立する統合先があるか」

を見る機能です。  
したがって final evaluation は、

- `candidate only`

で行います。

ここでも source は候補に入れません。  
merge は source baseline 比較そのものではなく、**external candidate の standalone viability search** として定義します。

### 再インストール先推定

`再インストール先を推定` は、ライブラリ内の既存譜面ファイルを、現在配置より健康度が高い導入先へ **譜面単体で** 移すための補助機能です。

したがって final evaluation は、

- `candidate only`

で行います。現在配置フォルダも同じ candidate-only 評価で baseline として測り、candidate list からは除外します。

自動適用するのは次をすべて満たす場合だけです。

- viable candidate が一意
- candidate の primary health が現在配置 baseline より高い
- metadata 判定が可能な場合は `TITLE / ARTIST` evidence が strong

複数候補、健康度が改善しない候補、metadata mismatch は `INSTL DST` を自動設定せず、warning と suggestion を残して手動選択に回します。

### source baseline

source baseline は不要ではありません。  
ただし ranking 本体に入れるのではなく、

- pending に残した package へ background auto-estimate を走らせるべきか

の判定にだけ使います。

つまり source baseline の役割は、

- 「推定候補の 1 つ」

ではなく、

- **「自動推定をそもそも開始すべきかの gate」**

です。

## 現在の設計原則

### 1. coarse filter と final evaluation を分離する

- coarse filter
  - external candidate を高速に絞る
- final evaluation
  - mode ごとの評価単位で順位付けする

### 2. mode ごとに評価単位を固定する

- `Normal`
  - `candidate + bundled`
- `ReinstallCorrection`
  - 既存ライブラリ譜面の再インストール先修正専用
  - `candidate only`
  - 現在配置 baseline より改善する一意候補だけ自動適用
- `MergeCandidateOnly`
  - `candidate only`

### 3. source は ranking 本体から外す

- candidate list
- selected candidate
- suggestions

には source を出さない。

### 4. source baseline は defer 判定専用

`innerWavHealthThreshold` を使い、

- source baseline が十分健康なら background auto-estimate を抑制する

だけに使う。

## mixed package の意味

### 通常推定

- 既所持譜面の実配置先再利用を優先
- 失敗したら `DeferredEstimateReason=InstalledDestinationResolveFailed` と警告を付け、未所持分の external install search へは進めない

### マージ推定

- 既存配置先再利用は shortcut として維持
- 失敗したら package 全体を `candidate only` で external merge search

この整理により、

- 通常推定 = 未所持補完
- マージ推定 = package 全体の統合先探索

という役割が明確になります。

## confidence の意味

現在の confidence は **external candidate だけ**で決まります。

- `High + destination`
  - viable external candidate が 1 位で明確
- `Low + suggestions`
  - viable external candidate 同士が僅差
- `High + no destination`
  - viable external candidate がない

source 前提の confidence reason は設計上不要になります。

## Relative Path 完了後の意味

`2026-04-23` 時点では、relative path は broad filter だけでなく final evaluation まで一貫しています。

- basename-only ref
  - basename 一致で `1` match
- path-aware ref
  - relative path 完全一致でのみ `1` match
  - basename-only matchにはフォールバックしない
- `Defined` / `Matched` / `CandidateCount`
  - basename-only + path-aware を合算した total resource count を基準にする

つまり relative path は「exact bonus」ではなく、**譜面に書かれている resource 指定方法の違い**として扱います。  
1 リソースの重みは basename-only / path-aware のどちらでも同じ `1` です。

## 今後の tuning 論点

- package-aware union 評価の重み調整
- `TITLE / ARTIST` frontier tie-break の重みづけと v2 の近似一致導入可否
- `Precision / Jaccard` の raw comparator の最終 tuning
- loose-file merge と package merge の wrapper 差
- fingerprint の導入判断

## P4 / P4b メモ

`TITLE / ARTIST` は全候補の主スコアには使わず、

- **P4**: resource 指標で僅差の上位 external candidate 群の tie-break
- **P4b**: 最終 1 位候補の妥当性検証

に使うのが現在の整理です。

- candidate 側は directory 配下全譜面の **最頻値 metadata profile**
- target 側も package / loose-file 単位の **最頻値 metadata profile**
- `TITLE` は exact + 軽量 fuzzy
- `ARTIST` は差分作者 suffix を文字列中から切る
- 役割は tie-break と最終 confidence 補助だけ

この切り方により、通常推定の `package_union` と merge の `candidate_only` を壊さずに、音源だけで並んだ無関係候補を metadata で断ち切れます。

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [install-estimation-performance-foundation.md](install-estimation-performance-foundation.md)
