# 導入先推定精度向上計画

## 完了状況

- `P1`: 実施済み
- `P3`: 実施済み
- `P2 前提整備（scan redesign）`: 実施済み
- `P2 本体（評価単位の再定義と余剰リソース評価）`: 実施済み
- `P2 最終調整（通常推定とマージ推定の意味分離）`: 実施済み
- `P4`: 未実施
- `P5`: 未実施

## 現在の整理

`2026-04-22` 時点の推定は、次のように整理されています。

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

## 今後の残課題

- `TITLE / ARTIST` tie-break
- fingerprint 系比較
- loose-file merge と package merge の wrapper 差整理
- 実機ケースでの raw precision / jaccard 重み調整

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
