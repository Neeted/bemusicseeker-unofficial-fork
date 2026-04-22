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

- `Normal` / `Fix`
  - `candidate + bundled`
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
- 失敗したら未所持分だけ external install search

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

## 今後の tuning 論点

- package-aware union 評価の重み調整
- `TITLE / ARTIST` frontier tie-break の重みづけと v2 の近似一致導入可否
- `Precision / Jaccard` の raw comparator の最終 tuning
- loose-file merge と package merge の wrapper 差
- fingerprint の導入判断

## P4 メモ

`TITLE / ARTIST` は全候補の主スコアには使わず、**resource 指標で僅差の上位 external candidate 群だけ**に使うのが現在の整理です。

- candidate 側は directory 配下全譜面の **最頻値 metadata profile**
- target 側も package / loose-file 単位の **最頻値 metadata profile**
- v1 は正規化後完全一致のみ
- 役割は tie-break と最終 confidence 補助だけ

この切り方により、通常推定の `package_union` と merge の `candidate_only` を壊さずに、音源だけで並んだ無関係候補を metadata で断ち切れます。

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
