# 導入先推定精度向上計画

## 完了状況

- `P1`: 実施済み
- `P3`: 実施済み
- `P2 前提整備（scan redesign）`: 実施済み
- `P2 本体（評価単位の再定義と余剰リソース評価）`: 実装済み
- `P2 最終調整（実機ケースでの順位・confidence チューニング）`: 継続中
- `P4`: 未実施
- `P5`: 未実施

現時点では、**曖昧な高スコア候補を自動確定しないこと** と、**保留画面で推定先の代表譜面 metadata を確認できること** まで入っています。  
加えて、**pending の `INSTL DST` 手動編集時にオートコンプリート型で上位候補をサジェストすること** と、**`confidence=Low` 行を警告色で可視化すること** まで入っています。  
一方で、`TITLE` / `ARTIST` tie-break や fingerprint 系はまだ入っていません。  
また、`P2` については **評価単位の再定義** まで入っており、現在は **package-aware union 評価の順位・confidence の実機チューニング** が残課題です。  
`2026-04-20` 時点の整理で本質だった **`candidate only` 評価** は解消し、現在の最終評価は `candidate + package bundled resources` を前提にしています。
加えて `2026-04-21` 時点では、**高ヘルス source baseline の pending package に対する background auto-estimate 抑制** と、**merge の source baseline 比較化** まで入っています。

## 今回実装したこと

### P1. 高スコア候補の可視化と confidence 導入

- 推定結果に `confidence` を追加した
  - `High`
  - `Low`
- 推定結果に上位候補情報を保持するようにした
  - 1 位候補
  - 2 位候補
  - 上位候補の評価指標
- `Low` 判定は「主要一致指標が完全同値で、差が `AudioFileCount` または `DirectoryPath` だけ」のケースを対象にした
- `confidence=Low` の場合は `INSTL DST` を**自動適用しない**ようにした
- その場合は `WARNING` に以下を出すようにした
  - 自動確定していないこと
  - 第1候補
  - 第2候補
- `estimate_install` / `auto_install_prepare` に low-confidence 判定のログを追加した
- pending の `INSTL DST` 編集時に、上位候補 3 件までをオートコンプリート型 UI でサジェストするようにした

### P3. 保留画面の確認導線強化

- pending 画面専用で以下の列を追加した
  - `INSTL DST TITLE`
  - `INSTL DST ARTIST`
- 推定先フォルダの代表譜面 metadata を **in-memory only** で解決する helper を追加した
  - 追加ディスク I/O は発生させない
  - `BMS` / `bmson` 混在可
- 自動推定で `INSTL DST` が入った場合は、その候補の代表 metadata を表示する
- `confidence=Low` で `INSTL DST` を自動適用しなかった場合も、1 位候補の代表 metadata は表示する
  - これにより、warning を見ながら目視確認できる
- 手動で `INSTL DST` を入力した場合も、その入力先に応じて `INSTL DST TITLE/ARTIST` を更新するようにした
- `confidence=Low` かつ複数候補あり未確定の行だけ、重複警告と同じ背景色で強調表示するようにした
- low-confidence 候補のどれかを手動選択した場合は、候補一覧と警告を維持したまま `INSTL DST` と代表 metadata を切り替えられるようにした
- 候補外の path を自由入力して確定した場合は、low-confidence 状態を解除するようにした

## 今回の実装で変えなかったこと

- 推定時の追加ディスク I/O は入れていない
- `bmson-only` フォルダを候補に含める既存改善は維持している
- 相対パスの本格対応は見送っている
- 第2候補の UI サジェストはまだ入れていない
  - 今回は warning と内部結果保持まで

## 目的

- `bmson` を含む Pending / install estimation で、連番リソースや高一致候補の競合による誤推定を減らす
- 自動推定の「当たりやすさ」だけでなく、「外した時に気付きやすいこと」も改善する
- 推定性能を大きく崩さず、既存の cache-based 推定方針の上で段階的に改善する

## 現状整理

- 現在の推定スコアは `Audio/Visual/Movie/OptionalImage` の
  - `Health`
  - `Matched`
  - `ExactMatched`
  - `Precision`
  - `Jaccard`
  ベースで、`TITLE` / `ARTIST` はまだ見ていない
- 余剰リソース評価は **candidate 単体ではなく `candidate + package bundled resources` の union** に対して行っている
- 追加音源を大量に含む元フォルダでも、譜面が定義している basename を十分満たしていれば高スコアになり得る
- `AudioFileCount` は順位決定の主軸から外している
- 高スコア候補が複数並んだ場合でも、1 位だけを `instl_dst` に入れて終わる
- `00.wav`, `01.wav` のような連番中心の譜面では、無関係なフォルダでも高一致になりやすい
- 相対パスは snapshot / 正規化には乗っているが、推定精度改善の主材料としてはまだ使っていない
- source folder は通常候補と同じ list 上で比較する
- source 1 位時は
  - 非 source 候補と僅差なら `Low + non-source suggestions`
  - 明確優位なら `High + no destination`
  として扱う
- ただし pending に残っていても、source baseline が `innerWavHealthThreshold` 以上なら background auto-estimate は走らせない
  - `DeferredEstimateReason=HealthySourceBaseline`
  - `INSTL DST` / suggestion / warning / 推定 metadata は空に保つ
  - 必要なら手動 `マージ先を推定` で source baseline 比較を行う

## 今回判明した誤推定パターン

`2026-04-20` 時点で、以下のようなケースを確認した。

- 対象譜面:
  - `D:\DOWNLOAD\#作業\■\DRIVE-DOWNLOAD-20260419T182728Z-3-001\007494\TAKETORIHAPPY_PTCG\竹取はっぴー(potechang).bme`
- 本来推定されてほしい導入先:
  - `D:\BMS\0 ■OTHER\[ZUN (Arr.sun3)] 竹取はっぴー`
- 実際のログ:
  - `confidence=High`
  - `confidenceReason=selected_source_directory`
  - `candidateDirsAfter=2642`

このケースでは、元フォルダに追加音源が多数ある一方、手動で正しい導入先へ配置した後の health check は明らかに高かった。  
それでも推定が元フォルダへ倒れた理由は、現行ロジックが次の性質を持つためである。

- 推定は代表譜面 1 件の resource 定義 basename を基準にしており、余剰ファイルを減点しない
- `AudioFileCount` の降順 tie-break があるため、追加音源が多い候補が有利になることがある
- source folder は通常候補とは別に評価されて候補へ戻される
- source folder が最終 1 位になると `selected_source_directory` で即 return し、他候補の採用に進まない

つまり、この種の誤推定は「候補集合が足りない」のではなく、**余剰リソースを罰していないこと** と **source folder 優遇が強すぎること** が主因である。

### `P2` 実装後に残った確認ポイント

scan redesign と package-aware union 評価導入前の時点では、`2026-04-20 18:50` 時点のログでこのケースは未解決だった。

- 実際のログ:
  - `evaluationMs=84`
  - `confidence=High`
  - `confidenceReason=selected_source_directory`
  - `candidateDirsAfter=2645`
  - `selected dir=...\\taketorihappy_ptcg`
  - `audio=0/0`
  - `audioCount=0`
  - `audioPrecision=0`
  - `audioJaccard=0`
- しかも候補ログに出ているのは source folder 1 件だけで、`second=(none)` になっていた

この旧挙動から分かった問題は、「`Precision / Jaccard` の重み不足」だけではなく、**`candidate only` 評価 + threshold + source reinject** の組み合わせにあった。

実装上は次の流れになっている。

- 通常候補は `innerWavHealthThreshold` 以下だと `candidateInfos` から除外されていた
- その後で `sourceEvaluation` は別枠で `candidateInfos` に追加されていた
- その結果、通常候補が全落ちした場合でも source folder だけが 1 件残っていた
- source folder がライブラリ外で cache を持たない場合、`audio=0/0`, `audioCount=0` のような**実質無情報の候補**でも `selected_source_directory` で勝てていた

つまり、このケースの未解決要因は少なくとも 2 つある。

1. **候補絞り込み段階**
   - `innerWavHealthThreshold` により、本来比較すべき候補が P2 の並び替えまで到達していない可能性がある
2. **source folder 再注入段階**
   - source folder が無情報に近い評価でも、通常候補がいないと `selected_source_directory` で確定できてしまう

このため `P2` 本体では、

- `candidate + package bundled resources` 評価
- source の通常候補化
- threshold の auto-apply 安全弁化

までを実装した。  
今後の `P2` 調整では、この新しい評価単位の上で **順位式と confidence 条件の実機チューニング** を詰める。

## 基本方針

- まずは **誤推定を UI 上で見抜きやすくする改善** を優先する
- 次に **余分リソースの多い候補を落とす評価** を入れる
- その後 **TITLE / ARTIST の近似度** を上位候補同士の tie-break に限定して入れる
- より大きい改善案として、将来的に **譜面定義ベースの resource fingerprint** を併用する
- 相対パスの本格対応は今回はスコープ外とし、必要なら別計画に切り出す

## 優先度順の実装候補

### P1. 高スコア候補の可視化と confidence 導入

実施済み。自動推定が外れても気付きやすくし、即運用改善につなげる。

- 推定結果に `confidence` 概念を追加する
  - 1 位と 2 位の差分
  - 同点候補数
  - スコアが高くても曖昧かどうか
- confidence が低い場合は:
  - `WARNING` に「推定候補が複数あります」相当を出す
  - 今回は自動適用せず、手動確認へ回す
- 推定結果に第 2 候補・第 3 候補を保持できる形へ拡張する
- 保留画面に以下の確認情報を追加する
  - `INSTL DST TITLE`
  - `INSTL DST ARTIST`
  - 必要なら `INSTL DST CONFIDENCE`
- `INSTL DST TITLE/ARTIST` は、推定先フォルダの代表譜面 metadata を表示して「合っていそうか」を人間が確認できるようにする
- 手動 `INSTL DST` 入力時や候補選択時に、第 2 候補をサジェストできる土台を作る

実際に入ったもの:

- `confidence`
- low-confidence 時の warning
- 1 位 / 2 位候補保持
- `INSTL DST TITLE/ARTIST`
- `INSTL DST` 候補サジェスト
- low-confidence 行着色

今回未実施:

- `INSTL DST CONFIDENCE` 列
- サジェスト候補ごとの metadata 一覧表示
- 第2候補の context menu / tooltip

### P2. 余分リソースの少なさを評価へ入れる

前提整備と本体の評価単位再定義は実施済み。実機チューニングは継続中。

#### 今回入れた前提整備

- `Everything` と通常列挙で **同じ意味の推定用 cache** を作るようにした
- `sibling:` query を廃止した
- scan を `chart / audio / image / movie` の分離クエリへ置き換えた
- `BmsScanResult` を chart-directory keyed の hash-only shape に更新した
  - `AllResourceBaseNameHashesByChartDirectory`
  - `Audio/Image/Movie` の basename hash
  - `Audio/Image/Movie` の relative path hash
- resource は「存在ディレクトリ」ではなく **最長一致する chart directory** へ再集約するようにした
- `FilesByDirectory` 依存を source of truth から外し、`DirectoryResourceLookupCache` も hash-only entry 前提に寄せた
- root custom folder 出力先は scan roots から除外するようにした

#### この前提整備が必要だった理由

- dirty state の `P2` は、Everything 経路と通常列挙経路で scan shape が違うため安定して動かなかった
- 特に Everything 使用時は `FilesByDirectory` が空で、カテゴリ別評価や余剰リソース評価の土台が揃っていなかった
- 先に列挙基盤を揃えないと、`Precision / Jaccard` を導入しても経路差で誤動作しやすかった

#### P2 本体として実装したもの

- package 単位 snapshot
  - `DefinedResources`
  - `BundledResources`
  - `SourceCandidateResources`
- final evaluation の `candidate + package bundled resources` 化
- `CollectTargetResourceHashes(...)` の package aggregate 化
- source folder の通常候補化
- source 1 位時の `Low / no auto-apply`
- `innerWavHealthThreshold` の auto-apply 安全弁化

#### P2 最終調整として残っているもの

- `Precision / Jaccard` の最終的な順位調整
- source を suggestion 候補に含める UI/UX の妥当性確認
- 実機ケースでの `confidenceReason` と auto-apply 条件の詰め
- `TITLE/ARTIST` tie-break や fingerprint 併用を後段で入れるかの判断

#### `2026-04-21` 時点で追加した運用整理

- high-health source baseline package は、pending に残っても background auto-estimate を抑制する
- file package は source baseline 抑制対象外とする
- merge は `MergeSourceBaseline` として、source を baseline に non-source が materially 上回るかを見る
- 内部順位付けでは raw precision / jaccard を使い、ログ/UI 用の整数 `%` は維持する

#### `P2` 最終調整の前提として確定した方向

`2026-04-20` 時点の再整理では、`P2` は「候補順位の重みを少し動かす」だけでは足りず、**最終評価単位を `candidate + package bundled resources` に再定義する**方針で実装した。

実装した理由:

- 旧 `EvaluateCandidate(...)` は candidate directory 単体しか見ていなかった
- 追加音源つき差分では、正しい導入先でも package bundled resources を足さない限り低 health になりやすかった
- その状態で `innerWavHealthThreshold` による除外と source 再注入があるため、source が勝ちやすかった

設計整理は別紙 [導入先推定のあるべき設計メモ](install-estimation-target-design.md) に残しつつ、現在はその方針に沿った実装が入っている。  
次の作業は、この新設計を前提にした実機ログでの調整と残件整理である。

### P3. 保留画面の確認導線強化

実施済み。P1 と同じくらい優先度は高いが、UI 寄りなので分離して扱う。

- 保留一覧に、推定先の代表譜面 metadata を表示する列を追加する
  - `INSTL DST TITLE`
  - `INSTL DST ARTIST`
- 代表譜面は「そのフォルダで最も代表性の高い譜面」を 1 件選ぶ
  - 既存のフォルダ自動リネームで使っている metadata 選定ロジックの流用余地を確認する
- confidence が低い候補は、保留一覧上で視覚的に分かるようにする
  - 例: `WARNING`
  - 例: `INSTL DST` の tooltip / summary
- これにより、自動推定を止めなくても「目視での誤検出」に気付きやすくする

実際に入ったもの:

- pending 専用の `INSTL DST TITLE` / `INSTL DST ARTIST`
- 推定先フォルダの代表譜面 metadata 解決
- 手動 `INSTL DST` 入力後の metadata 再解決
- `INSTL DST` 編集時の候補サジェスト
- low-confidence 行の背景色強調

今回未実施:

- `INSTL DST CONFIDENCE` の視覚化
- tooltip / context menu などの追加 UI
- 自動リネーム用 metadata 選定ロジックとの共通化見直し

### P4. `TITLE` / `ARTIST` 近似度を tie-break に導入する

未実施。上位候補が複数並んだ時だけ使う軽量改善。

- 全候補には使わず、上位候補群だけに限定する
- `TITLE` は以下のような正規化を検討する
  - `(` `（` `[` `［` `-` 以降を弱める/無視
  - 大文字小文字・全半角・空白差を吸収
- `ARTIST` は以下のような正規化を検討する
  - `obj`, `notes`, `note` など差分作者 prefix を除去
  - `/`, `／` などの区切り以降を弱める/無視
- 候補フォルダごとに代表譜面 metadata を 1 件取り、その `TITLE` / `ARTIST` と対象譜面を比較する
- 近似度は tie-break 用に限定し、主スコア化はしない

### P5. resource fingerprint / Simhash 系の併用

未実施。中長期の改善候補。即効性より将来性重視。

- 譜面定義側の resource 名から fingerprint を作る
  - resource 名抽出
  - 重複除去
  - 正規化
  - ソート
  - 結合
  - 必要なら Simhash 化
- 候補フォルダ側にも対応する fingerprint を持ち、近さを比較する
- 格納先候補
  - `chart_digest_map`
  - `bmson_song`
  - もしくは別の app-owned cache
- 現在の「実配置ベースの推定」は残し、その前段または tie-break として併用する
- 追加リソース付き差分への強さと、譜面定義ベース比較の軽さの両立を目指す

## 補助案 / 別案

### A. 複数譜面 union ベースの代表 snapshot

- 現在は representative chart 1 件に寄っている
- パッケージ全体の resource 集合を union して使うと、誤推定が減る可能性がある
- ただしコストが上がるため、P1〜P4 の後に評価する

### B. 相対パス作品の観測強化

- 相対パスの本格対応は今回はやらない
- ただし、相対パスが精度低下要因になっているかを後から判断しやすいよう、ログ観測だけは維持・強化する

## 切り方の推奨

実装は次の単位で切ると進めやすい。

1. **P1 + P3 の最小版**
   - 完了
   - confidence
   - WARNING
   - 上位候補の保持
   - `INSTL DST TITLE/ARTIST`
2. **P4**
   - P2 の次に入れやすい
   - `TITLE/ARTIST` tie-break
3. **P5**
   - fingerprint 系

## 次に実装できそうなもの

優先度順では、次は `P2 本体の最終調整` が最有力です。

- 理由:
  - `P1` / `P3` と scan redesign で、安全側 UI と列挙基盤は整った
  - 一方で `竹取はっぴー` ケースでは、`P2` の順位付けに入る前に通常候補が落ち、source folder だけが残る可能性が高いことが分かった
  - したがって次は、`Precision / Jaccard` の重み調整だけでなく
    - 候補 recall の維持
    - source fallback 条件の抑制
    を含めて `P2` を仕上げる段階
  - その次の本命が、僅差候補をさらに崩す `TITLE/ARTIST` tie-break
  - `TITLE` / `ARTIST` は全候補に使うと重くなりやすいが、上位候補だけなら入れやすい

`P5` は有望ですが、別テーブルや追加カラム、再計算設計まで含むため、今の段階では中長期扱いが妥当です。

## 現時点の実装前提

- 推定時のディスク I/O は禁止
- 初期化時 / リロード時 / アプリ内 mutation 時の cache を使う方針は維持する
- `bmson-only` フォルダを候補に含める現行改善は維持する
- 相対パスの本格解決は今回の改善対象に含めない
