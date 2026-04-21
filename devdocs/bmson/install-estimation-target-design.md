# 導入先推定のあるべき設計メモ

## 目的

この資料は、`P2. 余分リソースの少なさを評価へ入れる` を **最小修正ではなく、あるべき設計へ寄せて再設計するための設計メモ** です。  
次に具体的な実装プランへ入る前段として、何を source of truth にし、どの段階で何を比較すべきかを整理します。

対象は主に、追加音源つき差分や package bundled resources を含む pending package の導入先推定です。

## 実装状況メモ

`2026-04-20` 時点で、このメモの主軸だった次の項目は実装済みです。

- `candidate + package bundled resources` を最終評価単位にする
- package 単位 snapshot を導入する
- source を通常候補と同じ list 上で比較する
- source 1 位時は `High + no destination` または `Low + non-source suggestions` に分ける
- `innerWavHealthThreshold` を比較前除外ではなく viable / auto-apply 安全弁へ寄せる

現在この資料は、**なぜその設計にしたかの背景整理** と、**今後の tuning 論点メモ** として残しています。

## 先に結論

現状の最大のズレは、**最終評価が「candidate directory 単体」を見ている**ことです。  
あるべき評価単位は、次の **配置後想定状態** です。

- `candidate + package bundled resources`

つまり、導入先推定は

- 「その directory は今どれだけ揃っているか」

ではなく、

- **「その directory にこの package を導入した後、譜面群はどれだけ健康になるか」**

を評価対象にすべきです。

この考え方に立つと、候補絞り込みと最終評価の役割は次のように分かれます。

1. 候補絞り込み
   - 全 chart directory を相手に重い評価をしないための coarse filter
2. 最終評価
   - `candidate + package bundled resources` の union を前提に、導入後状態を比較する

`竹取はっぴー(potechang).bme` のようなケースで source folder が勝つのは、今の実装が 2. を満たしていないためです。

## 現状の問題点

現状ロジックの詳細は [install-estimation-current-logic.md](install-estimation-current-logic.md) に整理済みですが、あるべき設計とのズレだけを抜き出すと次です。

### 1. 最終評価が `candidate only`

現在の `EvaluateCandidate(...)` は、

- 候補 chart directory 側の hash 集合
- 代表譜面 1 件の `ChartResourceSnapshot`

だけを比較しています。  
ここでは **package 側に同梱されている追加音源・追加画像・追加動画を候補へ足していません。**

そのため、正しい導入先が「ベース側の一部リソースしか持っていない」場合、

- 本来は package を足せば成立する
- しかし今は candidate 単体で不完全と判定される

というズレが起きます。

### 2. `innerWavHealthThreshold` が最終比較前の recall を落とし得る

現状は `candidateDirsAfter` の後、`primaryHealth <= 70` の候補を通常候補から除外しています。  
しかしこの `primaryHealth` も **candidate 単体評価** に基づいています。

つまり、

- package を足せば成立する候補
- でも candidate 単体では 70 を下回る候補

が、P2 の順位比較へ入る前に落ち得ます。

### 3. source folder は別枠で再注入される

通常候補が落ちても、source folder は別枠で `candidateInfos` に戻されます。  
そのため、通常候補が `candidate only` 評価で脱落すると、source だけが残って `selected_source_directory` になりやすくなります。

### 4. 代表譜面 1 件ベースでは package 全体の性質を反映しきれない

現在は package 全体ではなく、代表譜面 1 件から `ChartResourceSnapshot` を作っています。  
これは軽量ですが、

- package 内の複数譜面が別々の追加音源を参照する
- 差分群全体として初めて妥当な導入先が見える

ケースには弱いです。

## あるべき設計の基本方針

### 方針 1. coarse filter と final evaluation を明確に分離する

候補絞り込みと最終評価の役割を混ぜない。

#### coarse filter の役割

- library 全 chart directory を相手に重い比較をしない
- 明らかに無関係な directory を早期に落とす

#### final evaluation の役割

- package 導入後の状態を近似する
- source と destination を同じルールで比較する
- low-confidence 判定や自動適用判断の source of truth にする

### 方針 2. 最終評価単位を `candidate + package bundled resources` に変える

最終評価では、候補 directory 単体ではなく次の union を見る。

- `EffectiveResources(candidate, package) = CandidateResources ∪ BundledResources(package)`

この `EffectiveResources` を、譜面群が要求する resource 定義集合と比較する。

### 方針 3. source folder も特別扱いせず、同じ評価軸に載せる

source folder は「fallback だから最後に戻す」のではなく、

- 通常候補の 1 つとして比較する
- ただし library 外であることは confidence / auto-apply 側で考慮する

という整理に寄せるのが自然です。

少なくとも、**通常候補が `candidate only` 評価で全落ちした結果 source が勝つ**状態は避けるべきです。

## 評価対象の再定義

### 1. package bundled resources とは何か

この設計でいう `package bundled resources` は、**package を destination へ導入したとき一緒に持ち込まれる resource 集合**です。

重要なのは、これは単に

- source directory に存在する全ファイル

ではない、という点です。

source directory には、

- package 外の unrelated file
- 既にライブラリ側にある base chart
- 参考資料やゴミファイル

が混ざり得ます。

したがって bundled resources の source of truth は、次に寄せるべきです。

- package に属すると判定された chart/resource のみ
- すなわち **導入対象 package が運ぶ実体** のみ

### 2. package bundled resources の最小単位

実装計画に入る前の前提として、bundled resources は少なくとも次のカテゴリで持てる必要があります。

- `AllBaseNameHashes`
- `AudioBaseNameHashes`
- `ImageBaseNameHashes`
- `MovieBaseNameHashes`
- `AudioRelativePathHashes`
- `ImageRelativePathHashes`
- `MovieRelativePathHashes`

ここでの hash 体系は、scan redesign 後の chart-directory keyed cache と同じ規則を使う。

### 3. representative file ではなく package snapshot を導入する

あるべき設計では、最終評価用の target 側情報は **代表譜面 1 件** ではなく、package 単位の snapshot に寄せるのが自然です。

候補としては次の 2 段階があります。

#### 段階 A. package aggregate snapshot

- package 内の未所持譜面群から resource 定義を union
- `Audio/Image/Movie/OptionalImage` を package 単位でまとめる

#### 段階 B. representative file は coarse filter 用に限定

- 高速な candidate prefilter では代表譜面 1 件を使ってもよい
- ただし final evaluation は package aggregate snapshot を使う

この分離により、

- 高速化
- package 全体の妥当性評価

の両立がしやすくなります。

## あるべき推定フロー

次のような 2 段階構成が望ましいです。

### Step 1. package aggregate snapshot を作る

対象:

- pending package の未所持 file
- correction / manual re-estimate 対象 file 群

作るもの:

- package 定義側 resource snapshot
- package bundled resources snapshot

ここで定義側と bundled 側を分けて持つ。

### Step 2. coarse candidate filter

目的:

- final evaluation に回す directory 数を減らす

候補:

- library 内 chart directory
- 必要に応じて source directory

判定材料:

- package 定義側 resource hash のいずれかにヒットするか
- 相対パス hash も補助に使えるなら使う

この段階では recall 優先でよく、**70 閾値で強く落としすぎない**方がよい。

### Step 3. final evaluation with union

各 candidate について、次を作る。

- `EffectiveResources = CandidateResources ∪ BundledResources(package)`

比較対象:

- package aggregate snapshot の resource 定義

ここで算出する指標:

- `Matched`
- `ExactMatched`
- `Health`
- `Precision`
- `Jaccard`

ただしこの `Precision / Jaccard` は、**union 後の effective set** に対して計算する。

### Step 4. confidence / auto-apply

最終順位が出た後に、

- `High / Low`
- `ShouldAutoApplyDestination`
- suggestion 候補

を決める。

ここで初めて、

- source が library 外か
- source が最上位だが十分な裏付けがあるか
- 2 位との差が小さいか

などを判断材料にする。

## `竹取はっぴー` ケースで何が変わるか

現状:

- 正しい導入先は candidate 単体では不完全
- package bundled resources を足せば成立する
- しかし現在は足さない
- threshold で落ちる / source が残る

あるべき設計では:

- destination 候補に package bundled resources を足して評価する
- 追加音源ぶんは source だけの優位にならない
- destination 側でも導入後の充足率が上がる
- source と destination が、少なくとも「導入後の状態」という同じ土俵で比較される

つまり `竹取はっぴー` は、P2 の重み微調整より前に、**評価単位の再定義だけで結果が大きく変わる可能性が高い**です。

## source folder の扱い

source folder は今後、次のように整理するのが自然です。

### 1. source を候補から外す理由を減らす

source が本当に正しいケースもあるため、完全除外はしない。

### 2. source を特別 fallback にしない

現状のように

- 通常候補が全落ち
- source だけ再注入
- `selected_source_directory`

という流れは避けたい。

### 3. source が 1 位でも auto-apply 条件は別管理にする

source が 1 位でも、次のような扱いは十分あり得ます。

- `Confidence = Low`
- `ShouldAutoApplyDestination = false`
- 候補サジェストのみ出す

つまり source の順位と source の自動確定可否は分けて考えるべきです。

## 閾値の役割見直し

`innerWavHealthThreshold=70` は、現状では最終比較前の候補除外に使われています。  
しかし `candidate + package bundled resources` へ寄せる設計では、閾値の役割も見直した方がよいです。

望ましい方向:

- coarse filter では recall を落としすぎない
- final evaluation では union 後 health を見る
- `70` 相当の閾値は
  - 自動適用の可否
  - low-confidence 判定補助
  - 候補サジェスト表示条件
  の側へ寄せる

少なくとも、**正しい候補が比較前に消える**形は避けるべきです。

## 実装に向けた論点

次の実装プランで具体化すべき論点は主に 4 つです。

### 1. package bundled resources をどう構築するか

- package source path から毎回再スキャンするのか
- package 読み込み時に一度 snapshot 化するのか
- `BMSPackage` に持たせるのか、推定サービスで都度構築するのか

### 2. package aggregate snapshot をどこまで広げるか

- まずは basename hash union だけで始めるか
- relative path hash まで同時に入れるか
- optional image を image とどう統合するか

### 3. source と destination の評価対称性をどこまで保証するか

- source も `candidate + package bundled resources` で比較するのか
- source には bundled resources を重複加算しない扱いをどう定義するか

### 4. coarse filter をどこまで残すか

- basename hash hit だけで十分か
- relative path hit を補助に使うか
- 70 threshold を prefilter から外すか

## 次の実装プランで決めるべきこと

この設計メモを前提に、次の実装プランでは少なくとも次を決める必要があります。

1. package aggregate snapshot / bundled resources snapshot の型
2. coarse filter と final evaluation の責務分担
3. `EvaluateCandidate(...)` を union 評価へどう置き換えるか
4. source folder の新しい扱い
5. `innerWavHealthThreshold` の新しい位置づけ
6. 既存ログ / tests をどう更新するか

## 関連資料

- [install-estimation-current-logic.md](install-estimation-current-logic.md)
- [install-estimation-accuracy-improvement-plan.md](install-estimation-accuracy-improvement-plan.md)
