# 導入先推定 相対パス対応の前提整理

## 目的

Perf-3 に入る前提として、導入先推定における **relative path 対応を incomplete な下地状態から完了状態へ持っていくための整理資料**です。  
この資料自体は実装プランではなく、**優先順のフェーズごとに実装プランへ落とせる前提整理**を目的とします。

## 実譜面前提

実物確認用の相対パス譜面として、次を前提ケースにします。

- `D:\相対パス譜面\07_12_nm [修正後].bml`

この譜面は少なくとも現在の実装では推定がうまく決まらないケースです。  
resource 定義は相対パス前提で、たとえば次のような記述を大量に持ちます。

- `#WAV01 sound\bgm1.wav`
- `#STAGEFILE image\daida_lily.bmp`
- `#BMP02 clock\00_001_00.bmp`

手元確認では、この譜面は少なくとも

- `#WAV` 系が `1294`
- visual 系 (`#BMP` / `#BMPC` / `#STAGEFILE` / `#BACKBMP` / `#BANNER`) が `9606`

あり、`sound\...` / `image\...` / `bga\...` / `clock\...` といった **サブディレクトリ付き resource 定義が本体**になっています。

## 先に結論

この前提では、relative path は「basename に対する後段補正」ではなく、**candidate 探索入口から使う first-class key** として扱うのが自然です。

つまり今後の目標仕様は次です。

- `bgm1` と `sound\bgm1` は **別の lookup key**
- 譜面が `sound\bgm1` を要求しているなら、candidate 探索の時点で `bgm1` は候補にしない
- `sound\bgm1` は「ディレクトリ区切りを含むファイル名」くらいの扱いで、一貫して推定全段に流す
- basename-only の比較は、**譜面側が basename-only で定義している resource** に対してだけ使う

言い換えると、現在の

- broad filter は basename-only
- final scoring でだけ relative path exact を加点

という構造は、この相対パス譜面前提では不十分です。

## 現状整理

### 1. 既に入っているもの

relative path 対応の下地はかなり入っています。

- `ChartResourcePathNormalizer`
  - relative path 正規化
  - rooted path / `..` / 無効文字の除外
  - 拡張子 alias 正規化
- `BmsScanResult`
  - chart-directory keyed な `Audio/Image/Movie` relative path hash を保持
- `EverythingFileScanner` / `FastDirectoryFileScanner`
  - どちらも relative path hash を構築可能
- `DirectoryResourceLookupCache.Entry`
  - basename hash と relative path hash を両方保持
- `ChartResourceSnapshot`
  - target 側の `AudioRelativePathHashes` などを保持
- `PackageInstallEstimationSnapshot`
  - bundled resource 側の relative path hash を保持
- `BmsLibraryInstallEstimationService`
  - final scoring の `CountMatches(...)` で relative path exact を使う

つまり現在は、**relative path を作る・持つ・後段評価で使う** ところまでは入っています。

### 2. まだ incomplete なもの

一方で、候補探索入口や一貫性保証は未完です。

- `BMSDirectoryFileNameHash`
  - all-resource basename hash index のみ
- `DirectoryResourceLookupCache`
  - reverse lookup / warmup は all-base hash のみ
- `BmsLibraryInstallEstimationService`
  - broad filter は `EnumerateAllBaseNameHashes()` ベース
  - relative path は candidate 発見には使わず、後段の exactMatched でだけ効く
- Everything と fallback の parity
  - `--everything-verify` で比較はできるが、恒常的な自動テストではない
- 増分更新
  - install / merge / regroup 後の index 更新が、初期化時と同じ意味で relative path を扱えていることを十分に固定していない

つまり現在は、**relative path が full path-aware candidate selection に昇格していない** 状態です。

## 今回固定したい仕様前提

以下を相対パス対応の前提仕様として固定してから、フェーズを切るのが良いです。

### 1. lookup key の意味

- basename-only key
  - 例: `bgm1`
- path-aware key
  - 例: `sound\bgm1`

この 2 つは別物として扱う。

### 2. 譜面側 resource の意味

- 譜面が basename-only で `bgm1` を定義
  - basename-only key として扱う
- 譜面が `sound\bgm1` を定義
  - path-aware key として扱う
  - `bgm1` にフォールバックしない

### 3. 候補探索の意味

- path-aware resource があるなら、その resource は **path-aware key で候補探索**する
- basename-only resource は従来どおり basename key で探索する
- 最終的な candidate 評価も、この resource ごとの意味を保ったまま行う

### 4. 通常推定 / merge の違い

relative path の意味自体は通常推定でも merge でも変えない。

- 通常推定
  - `candidate + bundled`
- merge
  - `candidate only`

の違いはそのまま維持しつつ、relative path resource も同じ semantics で比較する。

## 問題の本質

今回の問題は単に「relative path hash を使っていない」ではなく、次の 3 層の不一致です。

### 1. 列挙とライブラリ構築の parity

- Everything
- fallback
- 増分更新

のすべてで、同じ chart-directory keyed resource view を作れているかが未固定です。

### 2. 索引と候補探索の parity

relative path hash を保持していても、

- broad filter が basename-only

のままだと、`sound\bgm1` を `bgm1` と同じ候補探索に流してしまいます。

### 3. 推定 semantics の parity

後段の scoring で relative path exact を見ても、

- 候補発見時点で basename-only candidate を広く拾っている

と、相対パス譜面に対して本来要らない候補が大量に残ります。

## 優先順のフェーズ整理

以下の順で切ると、各フェーズを独立した実装プランへ落としやすいです。

### Phase 1. Relative Path Semantics Freeze

最優先は、相対パスの **仕様固定** です。

スコープ:
- `bgm1` と `sound\bgm1` は別 key
- `sound\bgm1` は `bgm1` にフォールバックしない
- basename-only resource と path-aware resource を別 category として扱う
- この意味を docs とテスト前提に固定する

このフェーズで決めること:
- path-aware resource を basename-only candidate へ落としてよい例外があるか
  - 今回前提では **なし**
- optional image 系も同じ semantics に寄せるか
  - 原則は寄せる

完了条件:
- docs 上で仕様が一意
- 後続フェーズの実装プランが「意味変更」なしで切れる

### Phase 2. Scan / Cache Parity Completion

次に、ライブラリ構築経路の parity を固める。

スコープ:
- Everything scan
- fallback scan
- 初期化 / reload
- install / merge / regroup 後の増分更新

このフェーズの狙い:
- どの経路でも
  - chart directories
  - basename hashes
  - relative path hashes
  が同じ意味で揃うことを保証する

主な対応候補:
- Everything vs fallback の比較テスト追加
- relative hash を含む `BmsScanResultComparer` 前提のテスト固定
- 増分更新後の `DirectoryResourceLookupCache` / chart-directory keyed data の整合確認

完了条件:
- relative path を含むライブラリ構築結果に経路差がない
- `--everything-verify` 依存ではなく、テストで固定される

### Phase 3. Path-Aware Index / Broad Filter Completion

ここが実質的な本体です。  
relative path を **候補探索入口**に昇格させます。

スコープ:
- `BMSDirectoryFileNameHash`
- `DirectoryResourceLookupCache` reverse lookup
- broad filter

このフェーズの狙い:
- `sound\bgm1` を要求する譜面は、candidate 探索時点で `sound\bgm1` 相当の key を使う
- `bgm1` は別 candidate として扱う

主な対応候補:
- basename index と別に path-aware index を持つ
- あるいは `DirectoryResourceLookupCache` の reverse lookup を path-aware に拡張する
- broad filter を
  - basename-only refs
  - path-aware refs
  で分岐する

完了条件:
- path-aware resource がある譜面で、basename-only candidate が入口で落ちる
- 実譜面 `07_12_nm [修正後].bml` を前提にした候補探索テストを追加できる

### Phase 4. Estimation Semantics Completion

最後に、候補探索後の推定 semantics を relative path 前提で完成させる。

スコープ:
- `ChartResourceSnapshot`
- `PackageInstallEstimationSnapshot`
- `BmsLibraryInstallEstimationService`
- metadata / confidence に入る前の candidate 意味

このフェーズの狙い:
- relative path resource を basename-only resource の「exact 加点」ではなく、**本来別 resource** として扱う
- 通常推定 / merge / package bundled semantics を壊さずに path-aware 化する

主な対応候補:
- `CountMatches(...)` の resource 意味整理
- viability gate での relative path の扱い再整理
- path-aware resource が多い譜面での suggestion / low-confidence 期待値固定

完了条件:
- relative path 譜面で normal / merge の両方の推定挙動が仕様どおり
- `sound\bgm1` と `bgm1` の区別が final scoring まで一貫

### Phase 5. Cleanup / Legacy Removal / Perf-3 接続

Perf-3 に入る前の仕上げです。

スコープ:
- obsolete helper / verify-only 導線 / 一時互換コード整理
- relative path 前提の計測 / ログ整理
- Perf-3 へ渡す index / scanner 前提の整理

このフェーズの狙い:
- relative path 対応を「特殊ケース対応」ではなく通常状態へ昇格する
- Perf-3 の source surface / scanner / index 改善にそのままつなげる

完了条件:
- 主要 docs が relative path 対応後の実装を説明している
- Perf-3 は relative path semantics を前提に着手できる

## フェーズごとの優先順位

優先順は次を推奨します。

1. Phase 1: Relative Path Semantics Freeze
2. Phase 2: Scan / Cache Parity Completion
3. Phase 3: Path-Aware Index / Broad Filter Completion
4. Phase 4: Estimation Semantics Completion
5. Phase 5: Cleanup / Legacy Removal / Perf-3 接続

理由:
- まず意味を固定しないと、Everything parity と推定改善のどちらもブレる
- broad filter を変える前に、scan / cache 結果が経路差なく揃うことを保証したい
- 実際に推定が改善するのは Phase 3 以降だが、その前提は Phase 1 / 2 にある

## この資料から次に切る実装プラン

次に個別プラン化するなら、順序は次が自然です。

1. `Phase 1 + Phase 2`
   - relative path semantics 固定
   - Everything / fallback / 増分更新 parity テスト整備
2. `Phase 3`
   - path-aware index / broad filter の実装プラン
3. `Phase 4`
   - normal / merge / bundled semantics の実装プラン
4. `Phase 5`
   - cleanup と Perf-3 接続プラン

## 関連ファイル

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-performance-foundation.md](install-estimation-performance-foundation.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [../spec/TECH_SPEC.ja.md](../spec/TECH_SPEC.ja.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
