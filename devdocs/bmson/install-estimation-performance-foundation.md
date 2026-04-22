# 導入先推定 性能改善の前提整理

## 目的

この資料は、導入先推定の**性能改善実装プランへ入る前提**を整理するためのメモです。  
ここでは個別実装プランには入らず、次の 3 系統の改善について

- どこが現在のボトルネックか
- どの前提を先に固定するか
- 実装プランをどう分割できるか

を整理します。

対象は主に次です。

1. 推定ロジック自体の軽量化
2. source package 側の追加キャッシュ / 列挙基盤
3. pending estimate の package 間並列化

## 現状の支配コスト

`2026-04-22` の 100+ package 一括ドロップで、Perf-1 実装前後のログを比較すると次です。

### Perf-1 実装前

- batch 全体
  - `143` package が pending に入り
  - `1` package が high-health defer
  - `142` package が推定対象
  - total `elapsedMs=66025`
- batch 先読み demand build
  - `targetHashes=6596`
  - `builtMs=166`
  - reverse lookup 構築自体は支配的ではない
- 重い package
  - `candidateDirsAfter=7999 evaluationMs=475`
  - `candidateDirsAfter=6312 evaluationMs=401`
  - `candidateDirsAfter=6302 evaluationMs=306`
  - `candidateDirsAfter=5891 evaluationMs=279`
- 最悪ケース
  - `candidateDirsAfter=0`
  - `candidateDirs=30299`
  - `evaluationMs=899`
  - hash prefilter 不成立時の全件 fallback が大きい

### Perf-1 実装後

- batch 全体
  - `143` package が pending に入り
  - `1` package が high-health defer
  - `142` package が推定対象
  - total `elapsedMs=61008`
- batch 先読み demand build
  - `targetHashes=6596`
  - `builtMs=228`
  - ここは引き続き支配的ではない
- fallback
  - `candidateDirsAfterBroadFilter=0`
  - `candidateDirsAfterAudioGate=0`
  - `candidateDirs=0`
  - `evaluationMs=0`
  - `fallback=False`
  - つまり全件 fallback は消えている
- candidate の縮小
  - `9815 -> 2840 evaluationMs=170`
  - `7999 -> 1604 evaluationMs=240`
  - `4914 -> 2478 evaluationMs=126`
  - `5891 -> 3011 evaluationMs=146`
  - `6312 -> 3305 evaluationMs=171`
- 集計
  - `estimate_install` は `136` 件
  - `candidateDirsAfterBroadFilter` 平均は `2730.5`
  - `candidateDirsAfterAudioGate` 平均は `1509.0`
  - `evaluationMs` 平均は `58.6`
  - `candidateDirsAfterAudioGate >= 1000` が `102` 件
  - `>= 2000` が `39` 件
  - `>= 3000` が `16` 件

### ここから分かること

Perf-1 の効果は明確に出ている。

- 全件 fallback の尖りは消えた
- sparse case は `evaluationMs=0` で即終了する
- batch 全体も `66025 -> 61008` で約 `7.6%` 短縮した

一方で、Perf-1 後もなお支配的なのは

- reverse lookup の事前 build ではなく
- **audio gate 後でも数千件残る candidate を full evaluation していること**
- **それを package ごとに直列消化していること**

である。

## 関連クラス

- `BMSLibrary`
  - pending estimate batch 実行
  - `RunPendingEstimateExclusive(...)`
  - source baseline defer 判定
- `PendingInstallEstimateQueueProcessor`
  - background pending estimate queue
  - 単一 worker
- `BmsLibraryInstallEstimationService`
  - coarse filter
  - fallback
  - candidate evaluation
- `PackageInstallEstimationSnapshot`
  - source package surface / bundled resources / metadata profile
- `PackageInstallEstimationSnapshotBuilder`
  - source path の再帰走査
- `DirectoryResourceLookupCache`
  - destination directory 側 hash cache
- `EverythingFileScanner`
  - ライブラリ初期化時の高速列挙
- `FastDirectoryEnumerator`
  - Everything fallback / 汎用列挙基盤

## Perf-1 実装メモ

`2026-04-22` 時点では、Perf-1 として次を採用しました。

- `candidateDirsAfter=0` の全件 fallback は行わない
- `audioRefs > 0` の譜面では、audio 一致が candidate 成立の最低条件
- `audioRefs >= 2`
  - audio basename hash 2 件以上一致必須
- `audioRefs == 1`
  - audio basename hash 1 件以上一致必須
- `audioRefs == 0`
  - audio gate を適用しない

以降の論点 1 は、Perf-1 の設計根拠として読む。

## Perf-1 後の優先度見直し

Perf-1 前は

1. coarse filter / fallback
2. source package 側の追加キャッシュ / 列挙基盤
3. package 間並列化

の順で整理していた。

しかし Perf-1 後ログでは、次の 2 つが同時に確認できた。

- `candidateDirsAfterAudioGate` が `1000+` の package が多数
- `2000+` や `3000+` もまだ相当数ある
- `evaluationMs` 上位 package はそのまま `candidateDirsAfterAudioGate` の多さに引っ張られている
- 一方で `evaluationMs` 合計は `7973ms` で、batch 全体 `elapsedMs=61008` の約 `13%`

つまり Perf-2 以降の本命は 1 本ではなく、

- **evaluationMs を直接下げる候補数削減**
- **wall-clock 全体を縮める package 間並列化**

の 2 本として扱うのが自然である。

したがって、Perf-2 以降の優先度は次に見直す。

1. **Perf-2a: unified audio candidate gate**
   - `candidate self minimum match` と `innerWavHealthThreshold` ベースの viability を 1 helper に統合
   - 通常推定は `candidate + bundled`
   - merge は `candidate only`
2. **Perf-2b: pending estimate の package 間並列化**
   - read-only evaluate phase と apply phase の分離
   - bounded parallel
   - batch wall-clock を縮める
3. **Perf-3: source package surface / scanner 基盤整理**
   - source surface 構築コストの計測追加
   - 列挙基盤と snapshot build の見直し
4. **Perf-4: Perf-2a / Perf-2b の統合調整**
   - candidate 数削減と parallel 度のバランス調整
   - apply phase / progress / lock 再整理

つまり、Perf-1 後のログが示す次の本命は **候補数削減と package 間並列化の二本立て**である。

## 論点 1: ロジック軽量化

### 1.1 全件 fallback は不要とみなす

現状は coarse filter の結果 `candidateDirsAfter=0` になると、全 `allCandidateDirs` へ fallback しています。  
これは「source が要求する resource が 1 件も見つからないなら、宛先になり得ない」という考え方と噛み合っていません。

今後の前提として、**hash prefilter が 0 件なら全件 fallback しない**方向で整理する。

意図:

- 作品と無関係な全 directory 総当たりを避ける
- `evaluationMs=899` のような尖りを消す
- `no_viable_destination_below_threshold` へ早く落とす

### 1.2 audio 一致条件を coarse filter / viability 前提として強化する

候補 directory が title/image/movie だけで引っかかると、普遍的ファイル名や連番で大量候補が残りやすい。  
今後は **audio 一致を強く要求する**前提に寄せる。

固定したい前提:

- source の `audioRefs >= 2`
  - candidate は **audio basename hash 2 件以上一致必須**
- source の `audioRefs == 1`
  - candidate は **audio basename hash 1 件以上一致必須**
- source の `audioRefs == 0`
  - audio 条件は課さず、visual/movie/optional 経路で評価してよい
- source の `audioRefs > 0` なのに `audioMatched == 0`
  - 他の resource が一致していても candidate としない

この整理の意図:

- `title.png` や `black.png` のような普遍名だけで候補化しない
- 「BGM だけ 1 hash 合った」候補を切る
- ただし本当に 1 音源しか持たない譜面は除外しすぎない

### 1.3 coarse filter と final evaluation をさらに分ける

今後の実装プランでは、ロジック軽量化を次の 2 段に分けて考える。

1. **coarse filter 改善**
   - 全件 fallback 廃止
   - audio 最低一致数
   - 必要なら audio / visual / movie ごとの最低成立条件
2. **final evaluation 軽量化**
   - 候補数がまだ多いときの cheap first pass
   - full `EvaluateCandidate(...)` は上位 subset に限定

Perf-1 実装後は、次段の本命の 1 つを引き続き 2 に寄せる。

具体的には Perf-2 で、少なくとも次のいずれかを検討対象にする。

- broad prefilter / audio gate 後の **第2段 coarse filter**
- candidate ごとの **cheap upper bound**
- full evaluation 前の **cheap first pass**
- 上位 subset だけへ metadata / full `EvaluateCandidate(...)` を流す二段評価

ただし、これは wall-clock 全体の唯一の本命という意味ではない。

- candidate 数削減は `evaluationMs` を直接下げる
- package 間並列化は batch 全体の直列待ちを崩す

という役割分担で考える。

この資料の段階では、詳細実装には入らず「Perf-2a は候補数削減の第2段である」とだけ固定する。

### 1.4 `innerWavHealthThreshold` を前段 viability gate へ寄せる

Perf-1 では audio minimum match gate までを前段に入れたが、まだ

- audio gate を通る
- しかし最終的な audio health は `70` 未満
- それでも `Low` 候補や suggestion として残る

ケースがある。  
この種の candidate は、wall-clock と UI の両面で価値が薄い可能性が高い。

Perf-2a 修正後は、`innerWavHealthThreshold=70` を **より手前の viability gate** として使う。

前提は次で固定する。

- 通常推定
  - `candidate self minimum match`
  - かつ `candidate + bundled` の effective audio health が `70` 超
- merge 推定
  - `candidate self minimum match`
  - かつ `candidate only` の effective audio health が `70` 超

つまり threshold は、

- 現状
  - 「最終 confidence / viable 判定」
- 次段案
  - 「coarse filter と final ranking の中間に置く audio viability gate」

へ寄せる。

この案の狙いは 2 つある。

1. **性能**
   - final ranking / metadata / low-confidence 処理に流す candidate 数を減らす
2. **精度**
   - 最終配置でも成立しない candidate を warning/suggestion に残し続けない

### 1.5 前段 threshold gate をどう軽く入れるか

ただし、`innerWavHealthThreshold` をそのまま full evaluation 前に完全計算すると、cheap gate にならない。  
そのため Perf-2a 修正後は、**1 本の unified audio gate** に次の 2 条件を持たせる。

1. **candidate self minimum match**
   - Perf-1 の 1/2 件 audio basename minimum match を維持
2. **effective audio viability**
   - 通常推定は `candidate + bundled`
   - merge は `candidate only`
   で `health > 70`

ここで大事なのは、「audio gate の強化」を helper 数ではなく**条件の統合**として再整理することである。

- 音源が 0 一致なら即除外
- bundled だけで threshold を満たしても、candidate 自身に音源根拠がなければ除外
- さらに、**最終的な effective audio health が threshold を超えない candidate は除外**

Perf-2a 再修正では、この unified gate の**意味は変えず**、内部実装だけを cheap/heavy に最適化した。

- self minimum match
  - candidate audio basename を直接なめて、1 件または 2 件で early-exit
- effective viability
  - full matched count を最後まで出さず、`health > 70` に必要な matched 数へ届いた時点で打ち切る
- relative path exact
  - viability 側の rescue として維持

つまり、

- `ApplyAudioCandidateGate(...)` という 1 helper の形は維持
- ただし内部は
  - cheap self minimum match
  - heavier viability check

の構造へ戻して、2段ゲート時より悪化した coarse filter CPU を取り戻す方針にした。

この再修正は性能最適化のみであり、

- candidate の意味
- normal / merge の mode 差
- metadata tie-break / validation
- confidence / warning / suggestion の semantics

は変更しない。

### 1.6 `innerWavHealthThreshold` 前倒しの位置づけ

この案は、`innerWavHealthThreshold` の意味そのものを変えるのではなく、

- 「宛先として有効なのは、移動後に audio health が threshold 以上になる candidate」

という既存の viability 意味を、**前段候補除外にも使う**ものと整理する。

つまり Perf-2a 修正後は、

- broad prefilter
- unified audio gate
- full evaluation / metadata / confidence

の順へ寄せるプランとして扱う。

### 1.7 Perf-2a 再修正後の実測

Perf-2a は一度、

- `candidate self minimum match`
- `effective audio viability`

を 1 helper に統合したが、内部実装が full count 寄りになったことで coarse filter CPU が悪化した。

同一条件の `pending_estimate_batch done source=auto_install` 比較は次のとおり。

- 2段ゲート時
  - `elapsedMs=55017`
- unified 直後
  - `elapsedMs=62021`
- Perf-2a 再修正後
  - `elapsedMs=50952`

つまり、

- unified 直後比で `-11069ms`
- 2段ゲート時比でも `-4065ms`

まで戻せている。

ここで重要なのは、**候補の意味や low-confidence 件数を変えずに速くなっている**こと。

- 2段ゲート時
  - `lowConfidence=107`
- unified 直後
  - `lowConfidence=107`
- Perf-2a 再修正後
  - `lowConfidence=107`

代表ケースでも、full evaluation へ流す候補数は実質同じだった。

- `9815 -> 2840 -> 1`
  - 再修正後は `9815 -> 1`
- `7999 -> 1604 -> 0`
  - 再修正後は `7999 -> 0`
- `5891 -> 3011 -> 175`
  - 再修正後も `5891 -> 175`
- `6312 -> 3305 -> 158`
  - 再修正後も `6312 -> 158`

つまり Perf-2a 再修正の本質は、

- unified audio gate の**仕様を変えず**
- old 2段ゲート時の **cheap/heavy 構造だけを内部へ戻した**

ことにある。

### 1.8 Perf-2a 再修正から得られたこと

今回の比較で確認できたことは次の 2 点。

1. `1 helper 化` 自体が遅いのではない
   - 遅かったのは、self minimum match と viability をどちらも full count 寄りで計算していた実装
2. coarse filter は、候補数だけでなく **候補を減らすまでの CPU コスト**も重要
   - cheap phase は early-exit
   - heavy phase も threshold 到達 boolean に留める
   という構造が効く

したがって、今後の性能改善でも

- 仕様統合
- helper 統合

を行う場合でも、cheap phase を消して full count 計算に寄せないことが重要である。

## 論点 2: source package 側の追加キャッシュ / 列挙基盤

### 2.1 現状の source surface 構築

現在の package-aware 推定では、`PackageInstallEstimationSnapshotBuilder` が source path を再帰走査して

- target chart 群
- bundled resources
- source candidate surface

を構築する。

`BMSPackage` 側には surface snapshot cache があるため再利用はされるが、**初回構築時の再帰列挙コスト**は残る。

### 2.2 Everything / Fast 列挙基盤との関係

ライブラリ初期化では

- `EverythingFileScanner`
- `FastDirectoryEnumerator`

を使った高速列挙経路が既にある。  
性能改善では、package source surface 構築もこれらと**別実装の個別再帰走査**として持つのではなく、

- package source surface scanner
- library scanner

が共通の列挙基盤を使う方向を検討する。

### 2.3 前提として固定したいこと

- package source surface の列挙は、今後 **scanner abstraction** として扱う
- 実装経路は
  - Everything 利用可能時
  - FastDirectoryEnumerator fallback
  の二段構成を基本とする
- ただし package source surface は library scan と違い
  - roots が drop source 単位
  - pending 中だけ必要
  - install surface 形に即した hash/materialization が必要
  なので、完全共有ではなく**共通列挙 + 個別集約**に寄せる
- つまり `EverythingFileScanner` の chart-directory keyed 結果をそのまま使い回すのではなく、
  **共通化するのは列挙基盤と path/hash 正規化まで**
  とする

### 2.4 実装プラン上の分割案

後続の実装プランでは、追加キャッシュ系を次の 2 本に分けられる。

1. **列挙基盤の一般化**
   - Everything / Fast で package source surface を列挙できるようにする
2. **surface snapshot cache の改善**
   - package 単位 cache の invalidation / 再利用 / warmup を見直す

## 論点 3: package 間並列化

### 3.1 現状は package 間が直列

現状の background pending estimate は

- `PendingInstallEstimateQueueProcessor`
  - 単一 worker
- `ProcessPendingInstallEstimateBatch(...)`
  - batch 内 `foreach (package)`
- `RunPendingEstimateExclusive(...)`
  - 各 package ごとに排他

という構造で、**package 間は完全直列**になっている。

一方で package 内の candidate 評価はすでに `AsParallel()` を持つため、今後の並列化本命は **package 間**である。

### 3.2 ただしそのまま package 並列化はできない

今の `SearchEstimatedInstallationDirectoryCore(package)` は

- pending list
- install DB
- warnings / suggestions / metadata

の更新まで含めた一塊の処理で、複数 lock の内側で動いている。

そのため、package 並列化は次の分解を前提に考える必要がある。

1. **read-only phase**
   - snapshot 構築
   - candidate filter
   - candidate evaluation
   - metadata validation
   - `InstallEstimationResult` 作成
2. **apply phase**
   - `BMSFile` への反映
   - warning / suggestion / metadata 同期
   - queue progress 更新
   - regroup 連携

並列化するのは 1 に限定し、2 は順序制御つきで適用するのが基本方針になる。

### 3.3 package 間並列化で先に定義しておく前提

後で実装プランに落とす前に、次を前提として固定する。

- 並列化対象は **background pending estimate のみ**
- 手動 `インストール先を推定` / `マージ先を推定` は従来どおり優先される
- `KeepInstallablePackagesPending` や source baseline defer で
  - 新規インストール相当 package
  - defer package
  が混ざるため、queue 順序は package 単位で保持する
- UI progress は batch 順の `completed / total` を維持し、内部の worker 並列数は直接見せない
- apply phase は deterministic にする
  - 同一 batch 内で package 完了順が前後しても、表示上の不整合が出ないようにする

### 3.4 並列化の分割案

後続の実装プランでは package 間並列化を次の 2 段で切れる。

1. **評価 phase の並列化**
   - `InstallEstimationResult` 作成までを bounded parallel
2. **queue / apply / progress モデルの再整理**
   - 結果反映順
   - manual estimate との排他
   - regroup タイミング

## 優先度を決めるための前提

後続の実装プランを優先度順に切るため、次の判断基準を採用する。

### A. まず wall-clock を大きく削れるもの

- 全件 fallback 廃止
- audio 一致条件強化

これは candidate 数を直接減らせるため、最優先候補。

### B. 次にログへ出ていない source surface コスト

- package source surface の列挙基盤整理
- 初回 snapshot 構築コストの可視化

ここは `evaluationMs` に出ないので、別計測を入れた上で着手順を決める。

### C. package 間並列化が次の本命候補
Perf-1 後ログを見る限り、package 間並列化は依然かなり有力である。

一方で、その前提として

- source surface 構築の未可視コスト
- audio gate 後も多い candidate 数

を先に詰めた方が安全である。

package 間並列化は効果が大きい可能性がある一方で、

- lock / apply 順
- UI progress
- manual estimate との競合

の設計コストが大きい。  
よって、今後は

1. package 間並列化
2. source surface 可視化 / scanner 整理

を次段候補として扱う。

## 追加で必要な計測

後続の実装プランへ入る前に、少なくとも次の計測があると判断しやすい。

- `package_surface_build_ms`
  - package source path 列挙
  - bundled/source surface hash 構築
- `candidate_prefilter_ms`
  - reverse lookup + coarse filter
- `candidate_evaluate_ms`
  - 現行 `evaluationMs`
- `candidate_count_before`
- `candidate_count_after`
- `fallback_used`

この資料の段階では「必要な計測項目」を定義するに留め、実装プラン側で導入判断する。

## 後続プランの分け方

この資料を前提に、後続の性能改善プランは少なくとも次の 5 本へ分割できる。

1. **Perf-1: coarse filter / fallback 見直し**
   - 全件 fallback 廃止
   - audio 最低一致数
   - candidate 数削減
2. **Perf-2a: mode-aware audio viability gate**
   - `innerWavHealthThreshold` を前段 candidate 除外にも使う
   - 通常推定は `candidate + bundled`
   - merge は `candidate only`
3. **Perf-2b: pending estimate package 間並列化**
   - read-only evaluate phase 分離
   - bounded parallel
   - batch wall-clock 短縮
4. **Perf-3: package source surface / scanner 基盤整理**
   - source path 列挙の一般化
   - snapshot build 計測
   - cache 改善
5. **Perf-4: Perf-2a / Perf-2b 統合調整**
   - apply phase / progress / lock 再整理
   - candidate 数削減と parallel 度のバランス調整

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
- [../spec/TECH_SPEC.ja.md](../spec/TECH_SPEC.ja.md)
