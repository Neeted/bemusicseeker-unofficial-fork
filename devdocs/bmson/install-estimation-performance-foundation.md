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
   - 実施済み
   - background batch を `evaluate parallel / apply serial` に再編
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

### 3.4 Perf-2b で採用した実行モデル

`2026-04-22` 時点では、background の `pending_estimate_batch` は次の順で動く。

1. demand build
2. immutable request 準備
3. bounded parallel evaluate
4. request 順の serial apply
5. regroup

並列化するのは **read-only evaluate phase のみ**で、次は従来どおり serial に保つ。

- `BMSFile` への適用
- warning / suggestion / metadata 同期
- progress 更新
- package-level の `estimate_install ...` ログ出力
- regroup

また、Perf-2b では nested parallelism を避けるため、background parallel path の package 内 candidate 評価は `asParallel: false` に固定する。

manual estimate の優先順位は変更しておらず、batch 全体を `RunPendingEstimateExclusive(...)` で囲うことで、**manual estimate は background batch 完了待ち**のまま維持する。

### 3.5 Perf-2b の設定

Perf-2b では hidden setting として次を追加した。

- `PendingInstallEstimateMaxParallelPackages`
  - `0` は auto
  - auto は `min(4, max(1, Environment.ProcessorCount / 2))`
  - 実効値は `1..8` に clamp

この設定は UI には出さず、background pending estimate の package 間並列度だけを制御する。

### 3.6 Perf-2b で変えていないもの

Perf-2b はあくまで **background 実行モデルの変更**であり、次は変更しない。

- unified audio gate
- metadata tie-break / metadata validation
- confidence / warning / suggestion の semantics
- source baseline defer 判定
- manual estimate / manual merge の動作意味

### 3.7 Perf-2b の実測

同一条件の `pending_estimate_batch done source=auto_install` 比較は次のとおり。

- Perf-2a 再修正後
  - `elapsedMs=50952`
  - `lowConfidence=107`
- Perf-2b 実装後
  - `elapsedMs=28628`
  - `lowConfidence=107`

つまり Perf-2b は、

- 推定件数
- defer 件数
- low-confidence 件数

を変えずに、batch wall-clock を **約 43.8% 短縮**している。

一方で package 単体の `evaluationMs` は増えている。

- Perf-2a 再修正後
  - `estimate_install` 136 件
  - `evaluationMs` 合計 `685`
  - 平均 `5.0`
- Perf-2b 実装後
  - `estimate_install` 136 件
  - `evaluationMs` 合計 `1605`
  - 平均 `11.8`

これは Perf-2b で background path の package 内 candidate 評価を `asParallel: false` に固定したためであり、**package 単体ではやや重くなるが、package 間並列化で batch 全体は大きく短縮する**という狙いどおりの結果である。

また、候補数そのものはほぼ不変だった。

- `candidateDirsAfterBroadFilter` 平均
  - `2730.5 -> 2730.5`
- `candidateDirsAfterAudioGate` 平均
  - `151.6 -> 151.6`
- `candidateDirsAfter=0` 件数
  - `28 -> 28`

つまり Perf-2b の効果は、candidate semantics や coarse filter 改変ではなく、**background 実行モデルの並列化そのもの**によって得られている。

### 3.8 Perf-3 に進む前提

Perf-2b 実装後は、候補数削減と background wall-clock 短縮の両方が一段落した。  
この時点で次の本命は、当初から残課題だった **source package surface / scanner 基盤整理**である。

Perf-3 では少なくとも次を前提にする。

- 目的は `evaluationMs` ではなく、**source surface 構築の初回コスト**を可視化して減らすこと
- まず追加計測を入れる
  - `package_surface_build_ms`
  - `package_surface_file_count`
  - `package_surface_hash_materialize_ms`
- そのうえで、`PackageInstallEstimationSnapshotBuilder.BuildInstallSurface(path)` の
  - 再帰列挙
  - hash/materialization
  - cache invalidation
  を分解して見る
- Everything と FastDirectoryEnumerator の共通化は
  - まず列挙基盤
  - 次に package surface 集約
  の順で考える

つまり Perf-3 は、Perf-2a / Perf-2b のように candidate を減らしたり並列度を上げたりする段ではなく、**source path 列挙と surface snapshot 構築を観測し、再利用可能な scanner / cache へ寄せる段**として扱う。

### 3.9 Ownership Fix 後の性能観測

`2026-04-23` に relative-path ownership fix を入れた後、correctness は改善したが batch wall-clock は再び悪化した。

同一条件の `pending_estimate_batch done source=auto_install` 比較:

- Perf-2b 基準ログ
  - `elapsedMs=28628`
  - `lowConfidence=107`
- ownership fix 後
  - `elapsedMs=39353`
  - `lowConfidence=106`

`demand_build` はほぼ同じだった。

- Perf-2b 基準ログ
  - `builtMs=163`
  - `entriesAdded=2342`
- ownership fix 後
  - `builtMs=160`
  - `entriesAdded=2342`

したがって増えた wall-clock の主因は demand build ではない。

さらに `estimate_install` 136 件を集計すると、候補数はほぼ不変だった。

- `candidateDirsAfterBroadFilter` 平均
  - `2730.5 -> 2728.4`
- `candidateDirsAfterAudioGate` 平均
  - `151.6 -> 151.5`
- `candidateDirsAfter` 平均
  - `151.6 -> 151.5`
- `candidateDirsAfter >= 100` 件数
  - `87 -> 87`

一方で `evaluationMs` は大きく増えた。

- Perf-2b 基準ログ
  - `evaluationMs` 合計 `1605`
  - 平均 `11.8`
  - candidate 1 件あたり約 `0.0779ms`
- ownership fix 後
  - `evaluationMs` 合計 `6117`
  - 平均 `45.0`
  - candidate 1 件あたり約 `0.2969ms`

しかも、この batch で `pathAwareRefs > 0` の package は `1` 件だけで、その package の `evaluationMs` は `0` だった。  
つまり悪化の本体は「path-aware 譜面を 1 件拾ったこと」ではなく、**ownership fix の追加コストが basename-only package 全体にも乗っていること**である。

現状コードから見て、主な増分要因は次の 2 つと考えるのが自然である。

1. `EvaluateCandidate(...)` の定数コスト増
   - aggregate view に加えて self-only view も毎 candidate で扱う
   - `SelfOwnedMatchedTotal` 用の extra match pass が増えた
   - `CandidateResourceView` も aggregate / self-only を両方抱える
2. `SuppressAncestorShadowCandidates(...)` の全候補走査
   - 候補数 `200~300` 規模でも、ancestor / descendant の有無に関係なく O(n^2) に近い比較を走らせる
   - 実ログでも `candidateDirsAfter=200+` の package 群で `evaluationMs` 増分が特に大きい

要するに ownership fix は、

- correctness には効いている
- しかし broad filter で候補を増やしたわけではなく、**candidate 1 件あたりの評価コスト**を押し上げている

という整理になる。

### 3.10 Ownership Perf Recovery 2nd Pass

この観測を踏まえて、Perf-3 に進む前に **ownership perf recovery 2nd pass** を入れた。  
これは既存の first pass の上に乗せる basename-only fast path で、`pathAwareRefs > 0` の package では strict relative-path final evaluation semantics を崩さず、`pathAwareRefs = 0` の package だけを軽くする段です。

この段でやったことは、ownership semantics を変えずに **常時コストだけを service 層で削る** ことです。

固定した前提:

- correctness は戻さない
  - root chart は descendant resource を見える
  - child-only chart は ancestor-shadow で守る
- broad filter / final scoring の意味は変えない
- `pathAwareRefs > 0` の package では final evaluation を strict relative-path semantics のまま維持する
- `pathAwareRefs = 0` の package では final evaluation と lookup-cache audio gate を basename-only fast path へ寄せる
- まず削るのは **普通の package にも常時乗っていた評価コスト**

実装した内容:

1. **self-only match の lazy 化**
   - `EvaluateCandidate(...)` では aggregate metrics だけを計算する
   - `SelfOwnedMatchedTotal` は未計算 sentinel で保持し、ancestor-shadow 比較が必要な候補だけで遅延評価する
2. **candidate hierarchy prepass**
   - audio gate 後の candidate set に ancestor / descendant 関係が 1 組もなければ、ancestor-shadow 自体を完全にスキップする
3. **chain-scoped shadow suppression**
   - 全候補総当たりはやめ、実際に hierarchy を持つ候補ペアだけを比較する
4. **bundled view の再利用**
   - `bundledResources` から作る view は candidate ごとに作り直さず、評価ループ外で 1 回だけ構築する
5. **basename-only fast path**
   - `pathAwareRefs = 0` の package は final evaluation で full `CandidateResourceView` を作らず、basename sets だけで評価する
   - lookup-cache audio gate でも basename intersection を使い、relative-path view の構築を避ける

追加した診断値:

- `evalMode`
- `candidateViewBuildMs`
- `candidateMatchMs`
- `candidateViewBuildCount`
- `candidateViewFallbackCount`
- `candidateDirsInHierarchy`
- `shadowSuppressed`
- `lazySelfOwnedCandidates`
- `shadowMs`

これで次の確認をログだけでできるようにした。

- hierarchy がない package では shadow path を通っていない
- self-only lazy 評価が一部候補にだけ限定されている
- 改善の本体が broad filter ではなく evaluation 側にある
- `pathAwareRefs = 0` の package では basename-only fast path が効いている

ここで重要なのは、ユーザーが直感している

- `basename.wav`
- `sound\\basename.wav`

の違いそのものよりも、**その差を守るための ownership / suppression 機構を全 package に常時適用していたこと**が性能悪化の本体だった点である。  
ownership perf recovery は、その常時コストを fast path で剥がすための段として整理する。

2nd pass ではその fast path を `pathAwareRefs = 0` に限定し、`pathAwareRefs > 0` の strict relative-path semantics は維持したままにしている。

## Relative Path 対応の性能整理

`2026-04-23` 時点の最新 batch 観測では、relative path 対応に伴う性能対策はひとまず収束したと整理してよい。

主要な比較:

- 最新 `install-performance.log`
  - `elapsedMs=29672`
  - `lowConfidence=106`
- ownership perf recovery 第1段階後
  - `elapsedMs=31829`
  - `lowConfidence=106`
- `Pref-2b`
  - `elapsedMs=28628`
  - `lowConfidence=107`

差分:

- 第1段階後 `31829` からは `-2157ms (-6.8%)`
- `Pref-2b` `28628` に対しては `+1044ms (+3.6%)`
- 目標としていた `30000ms` 未満は達成

`estimate_install start` の集計でも、改善の主因が candidate evaluation hot path にあることを確認できている。

- `evaluationMs` 合計
  - 第1段階後: `3449`
  - 最新: `706`
  - `Pref-2b`: `1605`
- `evalMode`
  - `basename_fast_path=135`
  - `relative_strict=1`
- `candidateViewBuildMs=0`
- `candidateViewBuildCount=0`
- `candidateViewFallbackCount=0`
- `candidateMatchMs` 合計 `88`
- `candidateDirsInHierarchy=0`
- `shadowSuppressed=0`
- `lazySelfOwnedCandidates=0`
- `shadowMs=0`

ここから言えること:

1. basename-only package の fast path は狙いどおり効いている
2. strict relative-path semantics は path-aware package にだけ残せている
3. evaluation hot path 自体は、むしろ `Pref-2b` より軽くなっている
4. 残る wall-clock 差分は、relative path semantics そのものや `EvaluateCandidate(...)` の重さではない

したがって、**relative path 対応による致命的な性能回帰は現時点ではない** と整理できる。  
以後の性能課題は「relative path 対応を成立させるための緊急 perf recovery」ではなく、batch orchestration / source surface / scanner / logging を含む通常の Perf-3 論点として扱う。

## Phase 6 / Perf-3 へ引き継ぐ前提

次段では、relative path 対応を特殊対応として持ち続けるのではなく、通常実装として整流化していく。

固定前提:

- relative-path semantics は凍結済み
- ownership semantics と ancestor-shadow guard は完了済み
- `pathAwareRefs > 0` package の strict relative-path final evaluation は維持する
- `pathAwareRefs = 0` package の basename-only fast path は維持する
- これ以上の perf 議論は relative path correctness の blocker 扱いにしない

Phase 6 / Perf-3 で扱うもの:

1. **Cleanup / Legacy Removal**
   - obsolete helper
   - verify-only 導線
   - 一時互換コード
2. **Perf-3: package source surface / scanner 基盤整理**
   - source path 列挙の一般化
   - snapshot / scanner / index 前提の整理
   - evaluation hot path の外側に残る wall-clock コストの観測
3. **docs / diagnostics の整流化**
   - 現状ロジックとログ項目の説明を一本化する
   - Phase 6 以降の改善対象を relative path 回帰対策と切り分ける

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-target-design.md](install-estimation-target-design.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
- [../spec/data-and-indexes.md](../spec/data-and-indexes.md)
- [../spec/TECH_SPEC.ja.md](../spec/TECH_SPEC.ja.md)
