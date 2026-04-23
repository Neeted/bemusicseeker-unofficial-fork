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

## Phase 1+2 完了時点の整理

`2026-04-23` 時点では、Phase 1+2 の前提は概ね整ったとみなせる。

固定できたこと:

- `bgm1` と `sound\bgm1` は別 key という semantics を docs で固定した
- `BmsScanResult`
  - chart-directory keyed な relative hash map を source of truth とする前提を整理した
- `DirectoryResourceLookupCache.Entry`
  - basename / relative の両方を持つ cache source of truth であることを docs / tests で固定した
- `BMSDirectoryFileNameHash`
  - Phase 1+2 では basename-only index であることを docs / tests で固定した
- `ChartDirectoryScanBuilder`
  - relative path fixture を使った managed scan の基礎テストを追加した
- `DirectoryResourceLookupCache`
  - `CreateFromScanResult(...)` と `AddDir(..., scanResult)` の parity をテストで固定した
- `BmsLibraryInitializationService`
  - `ApplyFileScanDiff(...)` 後に
    - `NextFolderAllFileList` は basename-only
    - `NextDirectoryResourceLookupCache` は relative hash を保持
    することをテストで固定した

まだ意図的に残していること:

- broad filter は basename-only のまま
- `sound\bgm1` を候補探索入口で `bgm1` から分離する実装は未着手
- Everything parity は opt-in integration test であり、常時 CI guarantee ではない

この状態で、**Phase 3 に進むための前提は十分揃っている**と整理してよい。
次に必要なのは semantics の追加議論ではなく、**path-aware key を candidate 探索入口へどう昇格するか**の設計である。

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

## Phase 3 完了時点の整理

`2026-04-23` 時点では、Phase 3 の主眼だった **candidate discovery / broad filter の path-aware 化** は完了したとみなせる。

固定できたこと:

- `ChartResourceSnapshot`
  - broad filter 用に basename-only ref と path-aware ref を category ごとに分けて持つ
- `DirectoryResourceLookupCache`
  - audio / image / movie の relative-path reverse lookup を持つ
- `DirectoryRelativePathHashIndex`
  - cacheless path 用の broad-filter 専用 relative-path index を持つ
- `BmsLibraryInstallEstimationService`
  - broad filter 入口で
    - basename-only refs
    - path-aware refs
    を分けて扱う
  - path-aware admission gate を通し、`sound\bgm1` を要求する target で `bgm1` only candidate を入口で落とす
- cache あり / cacheless の両経路で
  - path-aware ref は basename key へフォールバックしない
  - optional image は image relative lookup を使う
  という semantics を揃えた

Phase 3 時点で意図的に残していたこと:

- `CountMatches(...)` 自体の redesign
- final scoring における relative path の resource 意味整理
- viability / confidence 側への relative-path semantics 流し込み

この残件は、`2026-04-23` 時点で **Phase 4** として完了した。

また、実ログ確認でも

- `D:\相対パス譜面\07_12_nm [修正後].bml`
- `candidateDirsAfterBroadFilter=0`
- `candidateDirs=0`
- `confidenceReason=no_viable_destination_below_threshold`

となっており、**basename-only 候補へ誤着地する状態は解消**できている。  
Phase 4 ではここから先、relative-path 譜面を「誤推定しない」だけでなく、**正しい candidate を final scoring で拾える状態**まで進めた。  
ただしその後の実ログ確認で、`root chart + nested chart directory` が共存する package では、installed candidate surface が nearest-only ownership のまま残っており、root candidate が broad filter で 0 件になる gap が見つかった。  
したがって relative-path semantics 自体は完了したが、**candidate surface ownership** は次段の別フェーズとして扱う。

### Phase 4. Estimation Semantics Completion

最後に、候補探索後の推定 semantics を relative path 前提で完成させた。

スコープ:
- `ChartResourceSnapshot`
- `PackageInstallEstimationSnapshot`
- `BmsLibraryInstallEstimationService`
- metadata / confidence に入る前の candidate 意味

Phase 4 で固定したこと:

- basename-only ref
  - basename 一致で `1` match
- path-aware ref
  - relative path 完全一致でのみ `1` match
  - basename-only matchへはフォールバックしない
- `Matched`
  - ref を 1 件ずつ数える per-ref semantics に置き換えた
- `ExactMatched`
  - public shape 互換のため残すが、現在は `Matched` と同値
  - comparator の独立 bonus 軸からは外した
- `Defined` / `Matched` / `CandidateCount`
  - basename-only + path-aware を合算した **total resource count** を基準に統一した
- viability / unified audio gate
  - basename-only ref は basename 一致
  - path-aware ref は relative path 完全一致
  - `health > 70` も同じ per-ref semantics で判定する
- normal / merge
  - `candidate + bundled` / `candidate only` の mode semantics は維持
- cache あり / cacheless
  - broad filter だけでなく final evaluation も同じ semantics に揃えた

完了条件:

- relative-path 譜面で normal / merge の両方の推定挙動が仕様どおり
- `sound\bgm1` と `bgm1` の区別が final scoring / viability まで一貫
- basename-only 誤候補に行かず、正しい path-aware candidate がある場合は拾える

### Phase 5. Ownership Completion

Phase 4 の後で見つかった、installed candidate surface の ownership gap を埋めるフェーズです。

スコープ:
- `ChartDirectoryScanBuilder`
- Everything bridge
- `BmsScanResult`
- `DirectoryResourceLookupCache`
- `DirectoryRelativePathHashIndex`
- ancestor-shadow rule を含む install estimation の candidate suppression

このフェーズの狙い:
- descendant resource を **all ancestor chart directories** から見える aggregate ownership にする
- 同時に nearest-only の **self-only ownership** も保持する
- root candidate が descendant resource を見えるようにしつつ、child-only chart が親 candidate に食われないようにする

完了条件:
- root-style path-aware chart で root candidate が broad filter を通る
- child-style / basename-only chart で child candidate が親 aggregate candidate に押し負けない
- Everything / fallback / install / merge の各経路で aggregate + self-only の二重 semantics が揃う

### Phase 6. Cleanup / Legacy Removal / Perf-3 接続

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
5. Phase 5: Ownership Completion
6. Phase 6: Cleanup / Legacy Removal / Perf-3 接続

理由:
- まず意味を固定しないと、Everything parity と推定改善のどちらもブレる
- broad filter を変える前に、scan / cache 結果が経路差なく揃うことを保証したい
- 実際に推定が改善するのは Phase 3 以降だが、その前提は Phase 1 / 2 にある

## 次に切るプラン

Phase 1 から Phase 4 までは完了しており、次に切るプランは **Phase 5 / Ownership Completion** が自然です。

主論点:

1. aggregate ownership と self-only ownership の二重 view 導入
2. root chart と nested chart directory が共存する package の candidate surface 修正
3. ancestor-shadow rule で child-only chart を親 aggregate candidate から守る

つまり今後は、「relative path をどう実装するか」ではなく、**relative path semantics が完了した状態で installed candidate surface をどう正すか** が主戦場になります。

## 関連ファイル

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-performance-foundation.md](install-estimation-performance-foundation.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [../spec/TECH_SPEC.ja.md](../spec/TECH_SPEC.ja.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
