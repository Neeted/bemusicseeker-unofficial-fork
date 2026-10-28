# 導入先の推定

## 目的と適用範囲

保留パッケージや選択譜面について、所持ハッシュまたは使用リソースから既存フォルダを選び、作品との関連性、導入後のリソース充足、各譜面の音源許可条件を分けて自動設定する仕様です。入力、候補抽出、順位、確信度、画面への適用を定めます。実際の導入の受付・保存・失敗は[変更操作](mutations.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

本資料の「候補」は既存の譜面フォルダです。「充足率」は要求リソースのうち導入後に利用できる割合、「自身の所有分」は子孫から集約された分を除いたフォルダ自身のリソースを意味します。

## 仕様

操作の受付分類・競合結果・必要継続の寿命は[競合ポリシー](../core/operation-concurrency-policy.md)を正本とします。以下は入力・処理・資源所有と結果の固有契約です。

### 保留一覧の選択譜面操作

推定済み候補と現在の配置pathは、成功したライブラリの改名・純移動へ同じ相対位置で追従します。候補を順位付けし直さず、推定時の表示根拠と警告Messageを維持します。削除・統合で候補が影響を受けた項目の全候補失効と、確定DSTを優先する状態遷移は[変更操作](mutations.md#操作ごとの反映範囲)に従い、新候補の取得は明示再推定で行います。

既存の推定対象群に確定DSTと空DSTが混在するとき、対象群に確定DSTが一つでも残る間はパッケージ再推定を見送る既存条件を維持します。候補失効だけを理由に未確定項目の部分推定、自動分割、確定DSTの自動解除を行いません。この場合は利用者が既存の「インストール先をクリア」で対象パッケージの全DSTを明示クリアし、その終端後に改めて明示推定します。全クリアは保留目録の解除ではなく、同じパッケージ・項目・入力元とinstall記録を残して登録時の元配置健康度へ戻します。DSTが残らない独立パッケージの通常再推定とは区別し、既所持・未所持の分類や既存の推定対象資格を広げません。

保留ルートと個別パッケージの操作バーは、中央一覧の選択譜面だけを既存の右クリック操作と同じ要求・終端へ渡します。「推定」は `RelatedOnly` の導入先検索だけを行い、自動導入へ続けません。「インストール」は選択譜面用の `ManualInstall`、「クリア」は保留譜面の導入先解除を実行します。未選択の兄弟を要求へ追加せず、同一パッケージ内の実処理範囲は既存のワークフローに従います。

メニューの無関係候補保持・新規導入は維持し、導入済み譜面の配置先修復は保留用解除から分離します。導入後は既存の `MainWindowPendingPackageMutationViewTerminal` とツリー選択planで画面を反映します。表示・選択・アクセシビリティは[一覧の操作バー](../ui/table-view.md#保留譜面の操作バー)、受付・Busyは[競合ポリシー](../core/operation-concurrency-policy.md)に従います。

### 入力とリソースキー

対象はパッケージまたは単独の譜面です。[取得済みの共通リソース結果](chart-model.md#譜面情報とリソース保守)の使用参照を `ChartResourceSnapshot` へ索引化し、複数譜面では検索キーを和集合にして音声・画像・動画・任意画像を分けます。分母は使用IDから解決した種別別のユニークな要求ファイルキー数です。未使用定義を含めず、同じファイルへの複数ID参照は一件とします。抽出条件は[軽量解析の使用リソース](parser-compatibility.md#軽量解析の使用リソース)に従います。原文・用途・解析状態は失わず保持し、未取得を保存主体やファイルから隠れて補完しません。

BMS/PMSの未定義WAV/BMP IDとBMSONの参照先ヘッダーのないBGA/LAYER/POOR IDは、必要集合へ加えず、健康度・不足警告・導入自動設定へ影響させません。定義済みの使用参照の不正パスや空name・取得失敗は正常な要求0件へ置き換えません。未使用定義だけの親参照は使用要求の非対応理由にしません。

キーは拡張子を除いた譜面相対のパスです。`foo.wav` と `./foo.wav` は `foo`、`sound/./foo.wav` は `sound/foo`、`sound/foo.v2.wav` は `sound/foo.v2` になります。`foo` と `sound/foo` は別であり、ファイル名だけに戻して一致させません。既に正規化した参照をパッケージ集約時に再正規化しません。

ディレクトリ型のパッケージでは、候補抽出・同梱充足・足切り・順位の要求集合も実導入のルート座標を使います。直下譜面の `kick` と `sub/chart.bms` の `kick` は、ルート要求では `kick` と `sub/kick` の二件です。候補や同梱の親直下・別の兄弟にある同名で子の要求を満たしません。取得済みの個別参照と譜面相対配置を一度捕捉し、検索キーと種類別hashだけを配置付きで集約します。原参照・用途・解析状態と譜面別評価の入力は変更しません。自動取り込みの早期完全音源判定、共有入力からの推定、候補編集の源側90%判定でも同じ座標を使います。編集では後続の健康度更新と相対位置の捕捉を共有し、追加の入力元取得を行いません。

資源の親名は[リソース索引](../core/data-and-indexes.md#リソース索引と所有関係)と同じ物理配置の識別を使います。`audio.ogg/tone.wav` の参照は同じ親の `tone.ogg` と一致しますが、別親 `audio.wav/tone.wav` だけを持つ候補とは一致しません。親名への拡張子別名化で候補を混入・脱落させず、元参照と既存の推定閾値を維持します。

drive rootの逆引き候補も絶対 `C:\` を保持し、非root現在位置へ依存する `C:` に変換しません。rootと通常子候補の祖先関係を同じ自己所有・関連性の抑制規則へ渡し、通常directoryとUNCの従来の同一性を維持します。

パス解析で独立した `..` セグメントが見つかった場合だけ、親参照の非対応理由を登録し、索引のキーへ入れません。`sound/../foo.wav` も対象ですが、名前中の `foo..bar.wav` は親参照ではありません。実際の親参照が対象に一件でも含まれる場合は推定せず、`UnsupportedResourcePath` を表示します。

`.wav`、`.ogg`、`.png` など拡張子除去後のキーが空になる参照は索引へ追加せず、親参照の理由にも使いません。種類不明だけでもその理由を生成せず、未知拡張子の既存の解析採用範囲、空文字・不正パス・ドライブ絶対パス・先頭区切りの既存の正規化を維持します。既に解析された非対応理由は単体・パッケージ集約へ引き継ぎ、CP932のデコード非対応を親参照へ置き換えません。

### 入力元に同梱されたリソース

通常の導入推定では、候補のリソース `C` とパッケージが持ち込む `BundledResources` の集合 `S` の和集合を評価します。手動推定では既所持だけのパッケージも同じ入口を使います。再導入先の修正では、候補のリソースだけを評価します。

各譜面の音源充足は、実導入と同じパッケージ内相対階層を保った最終譜面ディレクトリで判定します。候補と同梱の両索引をその位置に揃え、親直下にある同名音源で子譜面の不足を満たしたことにしません。単独譜面は導入先直下を使います。推定入力には各譜面の使用集合と相対配置を一緒に保持し、既存の物理配置や入力受理範囲は変えません。

保留項目の健康度と不足警告も同じ相対配置で評価し、自動許可の判定と一致させます。設定先、先頭候補、入力元の優先順は維持し、候補と同梱は導入先ルートの索引を共有します。パッケージを伴わない同じ項目の再評価や導入先のクリアでも、捕捉済みの同梱・完全性・相対配置を引き継ぎます。入力元へ戻す場合は実際の譜面親を使い、相対位置を二重に付けません。配置表示の更新で登録時の元健康度を書き換えません。

ディレクトリ単位のパッケージは、そのルートだけを再帰的に調べます。一ルートあたりの訪問上限は50000件です。上限を越えた時点で止め、部分結果を推定に使わず `SourceSurfaceScanLimitExceeded` を付けます。複数パッケージの合計件数ではありません。全件を先に取得する方式ではなく、訪問中に上限を確認します。

列挙の失敗分類、継続条件と不完全性は[上限付き入力元列挙](../core/path-length-and-io.md#推定用探索の上限)に従います。

上限超過以外の通常失敗では、取得済みの部分資源を推定入力として使う既存の方針を維持します。`PackageInstallEstimationSnapshotBuilder` は列挙の成否・完全性と観測済みの譜面・資源・追跡対象件数、不完全理由を診断します。診断先の失敗で推定条件を変えず、新しい拒否理由へ変換しません。この部分結果をライブラリの削除判定や索引置換の根拠には使いません。配置照合では観測済み資源を存在として扱い、候補と部分同梱のどちらにもない参照は未確認とします。完全な列挙と候補索引によって不存在を確定できる場合だけ欠損とします。全音源を観測済み資源で満たせる場合、列挙の不完全性だけを理由に自動確定を拒みません。

譜面の発見結果は、列挙の成功・完全性と全譜面の読取り成功が成立した場合だけ完全な一覧とします。取得できた所属列は保持しますが、部分列を全所持・全選択の証明へ昇格しません。フォルダ削除の前には対象だけを改めて確認し、[削除対象と部分失敗](mutations.md#削除対象と部分失敗)に従います。通常表示・推定のためのパッケージcacheへ削除許可の状態を持たせません。

単一BMS・BMSONと単独譜面では、親をパッケージ範囲として列挙しません。`SourceDirectory` は入力元の候補除外や診断に使いますが、同梱リソースと入力元候補の集合は別の空集合です。走査時間、件数、ハッシュ生成時間、訪問件数は0、走査方式は空、上限超過はfalseとします。相対参照があることを親走査の理由にしません。

### ライブラリ側の索引

リソース経路の候補は `DirectoryResourceLookupCache.Keys` に含まれる譜面フォルダです。リソースだけの下位フォルダは候補にせず、入力元自身も除きます。

照合の正本は音声・画像・動画別の相対キーと逆引きです。ファイル名だけの集合や全種類を混ぜた集合を、候補抽出・最終照合・音声条件の代わりに使いません。リソース経路は索引がない場合に推定不能とし、全ライブラリの走査に切り替えません。所持ハッシュによる候補選択自体は資源索引なしでも保持しますが、自動適用には各対象譜面の使用音源を確認し、設定した音源充足率を満たすことを要求します。起動時走査を無効にした設定では、推定できない場合があります。

### 入口ごとの候補

| 入口 | 候補と選び方 |
| --- | --- |
| 通常推定 | 外部の譜面フォルダを探す。自動・手動の対象資格は[自動推定と並列処理](#自動推定と並列処理)に従う |
| 既所持・未所持が混在 | 入力相対配置から求めた候補根ごとに異なる有効MD5を一票として数え、単独最多の根と未所持群とのStrongを検証する。同点ならその候補集合だけをリソース・基本情報で評価する |
| 全対象が既所持（手動のみ） | 全対象の異なる有効MD5の票が単独最多の候補根と、全対象とのStrongと同梱込みの音源完全性を検証する。同点ならその候補集合を評価する |
| 単独bmson | 所持先が一意ならそのフォルダを選択し、その譜面とのStrongを検証する |
| 再導入先修正 | 現在の配置先を除いた既存フォルダを評価する。現在配置の状態は改善判定の基準だけに使う |

混在パッケージでは、配置先候補が解決できない場合、または候補限定評価に必要な索引がない場合、通常探索へ切り替えず `InstalledDestinationResolveFailed` とします。単独最多・一意解決は候補選択の事実であり、自動設定の許可とは別です。その候補にも共通の分類・各譜面音源条件を適用し、非関連なら通常ドロップダウンから除外します。解決失敗へ変換したり、少数側や別のリソース候補へ探索し直したりしません。明示的な無関係候補保持モードでは同じ解決候補を残せます。

ハッシュ同点の候補限定評価では、その候補範囲だけを比較します。一意な主ハッシュ数は、この入口で取得処理が渡された場合だけ補助条件に使い、通常探索の全フォルダへ新たな取得を追加しません。曖昧候補自動適用設定の適用入口は維持し、Strongと各譜面音源条件を満たす先頭候補だけを自動設定できます。

### 所持位置からの候補根とハッシュ票

パッケージの導入先は、入力根からの相対配置を一度だけ連結する根です。入力根 `Incoming/Pack` の `sub/old.bms` と所持 `Songs/Song/sub/old.bms` が一致する場合は、所持譜面の親 `Songs/Song/sub` から相対directoryの `sub` を除き、`Songs/Song` を候補根とします。入力根・譜面・所持directoryは絶対パスを要求し、物理パスは既存の `OrdinalIgnoreCase` で比較します。入力根と譜面パスが同一なら単体として相対部分を空とし、それ以外は要素境界付きの厳密な入力根配下だけを使います。根直下も相対directoryは空です。

相対directoryの各要素を所持directoryの末尾と厳密に照合し、一致する部分だけを除きます。候補根と相対directoryの再結合が元の所持directoryへ戻ることを確認します。空・根外・不正な配置や末尾構成不一致は候補にせず、別の祖先探索、名前補正、根外をファイル名へ置き換える処理、別方式の再探索は行いません。候補根直下に所持譜面がなくても許可し、既知譜面directoryや登録BMSrootへの所属を追加条件にしません。入力元と導入先の同一・祖先・子孫および物理重複を拒否する既存の導入判定は維持します。ファイル存在確認、新しい資源索引、祖先の資源索引、Strong・各譜面音源条件の緩和は追加しません。

一要求の候補根ごとに、異なる有効primary hashを一票とします。BMS・BMSONとも空白でないMD5を使い、MD5なしでSHA256へ切り替えたり、新たに長さ・hex形式を検査したりしません。同じMD5の全入力相対位置と全所持位置を変換して根へ集約し、先頭entryだけで判断しません。同じMD5が複数根へ対応すれば各根へ一票を与えます。コピーの数・名前・入力順だけでは票は増えません。

`MatchedHashCount` は要求全体で有効な候補根へ一致した異なるMD5の数です。AへH1、BへH2なら根別は1票ずつ・全体は2件、H1がA/Bへ対応する場合は根別は1票ずつ・全体は1件です。`CandidateDirectoryUniquePrimaryHashCounts` は候補exact directory内の全所持MD5数という補助値を維持し、この票数や一致件数へ読み替えません。候補根直下に所持譜面がなければ補助値は0です。

全既所持パッケージの資源上書き先にも同じ根変換を使い、全譜面が一つの根へ戻る場合だけ許可します。一譜面が複数所持directoryへ一致する場合は、変換前に従来どおり見送ります。一方だけを根へ変換できても複数所持先の拒否は解除しません。

### 候補の絞込み

カテゴリごとの要求集合を `R`、同梱集合を `S`、候補集合を `C` とし、次を集計します。

| 値 | 定義 |
| --- | --- |
| 同梱で満たす数 `s` | `\|R∩S\|`。候補間で固定 |
| 候補自身の一致 `M` | `\|R∩C\|` |
| 同梱不足を補う数 `D` | `\|(R\S)∩C\|` |
| 導入後一致 `U` | `s+D` |
| 充足率 `H` | `U/\|R\|` |
| Jaccard | `M/(\|R\|+\|C\|-M)` |

要求キー群の逆引きをカテゴリごとにまとめて準備し、各要求キーを持つ候補の列挙でMとDを同時に数えます。入力元自身と候補限定範囲外を除き、同じフォルダの登録や同梱との重複を二重加算しません。音声・画像・動画・任意画像は別々に数えます。件数だけを読むための候補配列の複製や、全候補に対する要求キーの再走査は行いません。

音声要求があれば、候補自身の一致Mが1件以上、かつ同梱込みHが80%以上であることを要求します。候補単独の80%は要求しません。音声要求がなければ、要求のある画像・動画・任意画像の最大Hへ80%以上を適用します。全カテゴリの要求が0件ならリソースだけで新候補を作りません。所持ハッシュによる直接解決には、このリソース探索用の最低一致数・80%条件を課しません。

リソース条件を通った全候補のプロフィールを解決して分類し、通常モードの非関連除外、祖先抑制、順位と曖昧性を判定します。探索や分類を3件・10件などへ切りません。画面へ渡す最後の候補だけ上位3件にします。候補がなくても全ライブラリ走査や別モードへ切り替えません。

### 評価値と順位

主要リソース指標は音声H・音声M・画像H・画像M・動画H・動画M・任意画像H・任意画像Mの順です。要求0件のカテゴリは比較しません。補助リソース指標は音声・画像・動画・任意画像のJaccardの順です。比率は元の分子・分母を交差乗算して比較し、表示整数の丸めで同順位を決めません。適合率（Precision）は評価・比較・診断に使いません。

| 一操作の候補ポリシー | 順位 |
| --- | --- |
| 通常 `RelatedOnly` | 分類（Strong→Related）、主要リソース指標、許可された入口だけの一意主ハッシュ数、Jaccard、パス |
| 無関係候補保持 `KeepUnrelated` | 主要リソース指標、Jaccard、分類（Strong→Related→非関連群）、許可された一意主ハッシュ数、パス |

通常の自動・手動推定はStrongとRelatedだけを表示候補へ残します。ツリーの保留パッケージと一覧の保留譜面の「インストール先を推定（無関係候補を残す）」だけが保持モードです。UnrelatedとUnavailableの差でリソース順位を逆転させません。モードは要求からバッチ・サービスまで明示的に渡し、保存したり次の操作へ持ち越したりしません。再導入先修正のメニューへ複製しません。

同梱100%でも候補自身Mを比較でき、同梱の不足を補う候補はHで比較できます。リソースの集計・順位には同梱率別の別方式や重み付き合計を設けません。音源自動設定閾値も候補順位を変えません。

### 持ち込む使用音源によるRelated条件の選択

通常推定・候補限定・所持ハッシュの直接候補は、同じ対象群の使用音源要求の和集合を `R`、実際に持ち込む同梱音源を `S` とし、候補を加える前の `s=|R∩S|`、`n=|R|` から一度だけ条件を選びます。混在パッケージは未所持群、全既所持の手動推定は全対象を使います。要求が確定した `n>0` に対し、`100L*s >= 90L*n` を証明できる場合は `BothFields`、それ以外は `AnyField` です。Strongはこの切り替えより先に従来条件で判定します。

| 源側入力 | Related条件と診断 |
| --- | --- |
| 完全な同梱で89/100、899/1000、90/101 | AnyField。90%未満を確認済み |
| 90/100、900/1000、9/10、91/101 | BothFields。90%以上を確認済み |
| 使用音源要求が取得済みで0件 | AnyField。率は適用対象外であり、100%とは扱わない |
| 要求未取得・照合不能な使用音源・同梱未捕捉 | AnyField。率は確認不能であり、0%とは扱わない |
| 同梱が部分列挙で95/100を観測、要求は確定 | BothFields。観測下限だけで90%以上を証明できる。正確な率と入力面の完全性は未確認のまま |
| 同梱が部分列挙で89/100を観測、残り未確認 | AnyField。境界判定は確認不能であり、確定89%とは扱わない |
| 部分列挙でも全要求を観測 | BothFields。同梱だけで100%と確認できるが入力面の完全性は変更しない |

分母・分子は既存の正規化された使用キーの件数です。未使用定義、同梱の総件数、ファイルサイズ、候補の音源、配置先込み健康度、表示の丸めは使いません。単独譜面は既知の空同梱からAnyFieldを選び、親を列挙しません。既存の走査上限超過・非対応入力は推定を中止したままです。

この固定90%はメタデータ条件だけを選びます。リソース探索の80%・候補自身の音源一致1件以上、各譜面の自動設定90～100%とは独立し、自動推定の開始、新規導入の分類・許可も変更しません。再導入先修正は現在配置の充足率を流用せず、常にAnyFieldを明示します。KeepUnrelatedでも同じ規則で分類し、非関連候補を残す既存のリソース優先順位を維持します。通常候補が0件でもAnyFieldで再探索しません。

### 比較用の基本情報と導入先の代表ペア

推定対象はその要求で選んだ譜面集合、導入先候補は既存の実パス索引から取得した所持譜面集合を集計します。両側で次の同じ入力を使います。

| 形式 | TITLE | ARTIST |
| --- | --- | --- |
| BMS | `RawTitle` | `RawArtist`。別フィールドの `Subartist` を合成しない |
| bmson | `RawTitle` | 現在の `Artist`。副作者を含む合成値もそのまま正規化する |

BMSの別フィールドの副題やbmsonの副題・譜面名をTITLEへ合成しません。原題が空なら表示用 `Title` で補いません。bmsonのtitle省略・null・空文字も原題なしとして保持し、表示用の合成と入力の受理範囲は維持します。bmsonの `RawArtist` は比較に使わず、`Artist` が空でもそこから補いません。保存形式や保存・復元処理は変更しません。

TITLEとARTISTはNFKC・空白整理を行い、小文字化前の表記と、その `ToLowerInvariant()` による比較キーを対応させます。元の譜面値・保存値は変更しません。正規化の目的は候補の妥当性を照合することであり、完成した曲名・作者表記の抽出や括弧の修復は行いません。

ARTISTは先頭の無標識区間を保持し、歌唱・譜面・画像・映像等の副役割、または区切りスラッシュからの区間を除きます。後方に明示された音楽制作役割があれば、その役割の先頭から保持を再開します。制作役割でも必ず直前の保持区間を確定し、副役割の有無によって括弧や区切りの扱いを変えません。最後の制作語より前を丸ごと保護する処理は使いません。

役割名は大文字・小文字を区別せず、左がASCII英数字・`_`・`-` の語中では認識しません。有効な左境界の直後のハイフン1個は役割に含めます。日本語名直後の `源屋feat.ユスラ` は認識します。記号区切りは `.`・`:`・`;`・`,`・`=`・`/` とその組合せです。

| 分類 | 役割名 | 右側に要求する境界 |
| --- | --- | --- |
| 副役割 | `feat`、`featuring`、`obj`、`pmsobj`、`reobj`、`odj`、`bga`、`bgi`、`movie`、`mov`、`illust`、`illustration`、`vocal`、`vocals`、`差分`、`譜面` | 記号区切りまたは空白 |
| 副役割の短語 | `ft`、`vo` | 任意の空白に続く `/` 以外の記号区切り、または直結した `/` |
| 副役割の一般語 | `note(s)`、`object(ed)`、`bg`、`bms`、`chart(s)`、`graphic(s)`、`slice(d)`、`guitar(s)`、`pms`、`pattern(s)`、`sequence`、`lyric(s)`、`image`、`artwork`、`visual` | 短語と同じ記号区切り、または空白に続く `by` |
| 音楽制作役割 | `mix`、`mixed`、`remix`、`remixed`、`rmx`、`arr`、`arrange`、`arranged`、`arrangement`、`cover`、`covered`、`compose`、`composed`、`composer` | 終端・記号区切り・空白 |
| 明示表記を要求する音楽制作役割 | `music`、`midi`、`作曲`、`編曲` | 任意の空白に続く記号区切り、または空白に続く `by` |

表の `by` の右は終端・空白・記号区切りを要求します。短語・一般語の導入用 `/` は、直後が空白や `/` の場合も認識しません。`Frontier Lyric / obj.Diff` は名義を保持して独立スラッシュから除去し、`BMS/GYAON` は明示された副役割として認識します。

上記の役割語同士が、前後の空白を許す `&`・`+`、または両語へ直結した `/` でつながる複合役割は、最長の一つのマーカーとして扱います。音楽制作役割を含む `music & movie` や `Music/BMS/BGA` は一括して保持し、副役割だけの `Illust&obj` や `Bms&Bga` は除去します。名義や自由文を挟む役割の列は複合役割にしません。マーカーの導入記号列と `by` も一括認識し、`Remix / Pino` の導入スラッシュを別の切断位置にしません。

`FT 718`、`Key Note Team`、`Object`、`Frontier Lyric`、`BGAキャラ水着ガチャ` は役割と見なしません。副作者名中の `better music seter` のような裸の `music` でも保持を再開しません。スラッシュ単独で除去を始めるのは、先頭以外の括弧外にあり、前後どちらかが空白、または連続した `//` の場合です。`q/stol`、`Dango/Dx`、`1/8`、`/指紋`、括弧内の `An/レク` は保持します。`Nota/obj:みあ` は明示役割から除去できます。

`()`・`[]`・`<>`・`【】`・`《》` の対応は原文上のスラッシュの内外判定だけに使い、括弧内の副役割も除去します。各保持区間の末尾から空白・スラッシュ・開閉括弧を整理し、空白で独立した `. : ; , = / | & + -` の記号列も取り除いてから、空でない区間を空白1個で連結します。削除区間がない場合も、全体を同じ規則で整理します。名義に直結する句読点は保持するため、`SKY. obj:Diff` は `SKY.` になります。`|` 単独では除去区間を始めません。

| ARTISTの入力 | 小文字化前の照合ARTIST |
| --- | --- |
| `Artist feat. Guest Illust&obj:Diff` | `Artist` |
| `Artist feat. Guest Remixed by X` | `Artist Remixed by X` |
| `Artist feat. Guest (Remixed by X)` | `Artist Remixed by X` |
| `Artist (Remixed by X)` | `Artist Remixed by X` |
| `Aliesrite* (Ym1024 feat. Guest)` | `Aliesrite* (Ym1024` |
| `Aliesrite* (Ym1024)` | `Aliesrite* (Ym1024` |
| `BMS:ChartAuthor MIDI:Composer` | `MIDI:Composer` |

名義の括弧内にある作曲者名は保持し、閉じ括弧を復元する処理は持ちません。無標識の自由文から担当者を推定せず、制作役割の別表記の同一視や別名義の辞書も追加しません。一般のARTIST表示や別フィールドのSUBARTISTは、この処理で書き換えません。

TITLEは `()`・`[]`・`<>`・`【】`・`《》` の対応と入れ子を考慮した外側ブロック、`~`・`〜`・空白直後の `-` を区切り候補とします。一番右の区切り群だけを切断対象にし、囲みの間が空白だけなら同じ群として右から処理します。通常の文字列が挟まると別群になります。ハイフン囲みの中で文字・数字が直後に続く語中ハイフンは閉じ側にせず、`-J-Trance remix- [Another]` も同じ区切り群として扱います。先頭から始まる囲みは主題として保護し、閉じ忘れ・片側区切り・囲みの右側に続く文字も末尾切断の候補にします。主題全体の括弧整合性は要求しません。主題の括弧が交差していても対応する閉じ括弧を認識し、主題自体を修復・切断せずに末尾の差分だけを除去します。

削除対象に大小文字を区別しない部分文字列 `mix` があれば、その範囲の最も右の `mix` の直後で切断して終了します。`remix`・`Himix`・`mixed` も対象であり、単語境界は要求しません。これは曲名抽出ではなく、リソースで絞った候補を比較するキー生成です。難易度や差分作者の辞書、旧結果への代替経路は持ちません。

| 生TITLE | 小文字化前の照合・表示TITLE |
| --- | --- |
| `Ark (remix/5keys)` / `Ark (remix/10lines)` | `Ark (remix` |
| `Ark (mixed)` | `Ark (mix` |
| `夏草の線路 -J-Trance remix-` / `夏草の線路 -J-Trance remix- [Another]` | `夏草の線路 -J-Trance remix` |
| `Air -GOD-` | `Air` |
| `Air -Revolutionary.Himix- [7keys ANOTHER]` | `Air -Revolutionary.Himix` |
| `VIEW [Portable Disco Mix] [H14]` | `VIEW [Portable Disco Mix` |
| `[主題] [差分]` | `[主題]` |
| `【Freak《Show】Down》 [Another]` / `【Freak《Show】Down》` | `【Freak《Show】Down》` |
| `Nothingness[7A(LN)]` | `Nothingness` |
| `Title (A) 続き [差分]` | `Title (A) 続き` |
| `Title [差分] 追加表記` | `Title` |
| `~メッセージ~ [Another]` | `~メッセージ~` |

切断は生TITLEへ一回だけ適用します。閉じ括弧が残らない比較キーを許し、正規化済みキーへ再適用しません。

同一レコード由来の `(正規化TITLE, 正規化ARTIST)` を一組として集計します。TITLEが空のレコードだけを除外し、ARTISTの空文字は有効とします。件数最大の実在ペアを採用し、同数ならグループ内の最小パスの `OrdinalIgnoreCase` 順、続いて最小入力位置で決めます。比較用代表値は採用ペアの二要素です。例えばA/Xが3件、B/YとC/Yが各2件なら比較も表示もA/Xに由来し、独立最頻値のa/yを合成しません。

対象の実在最頻ペアは混在パッケージでは未所持群、全既所持パッケージでは全対象、単独・looseの既存個別評価では各譜面から作ります。所持多数で未所持群の不一致を埋めず、対象側全ペアとの総当たりへ広げません。候補側は既存実パス索引が返す子孫を含む所持範囲を維持します。

初回の発見でdirectory型とした一式も、この対象群の集約プロフィールで評価します。一式の全同梱を配置する範囲と、推定先を自動適用できる条件は別であり、自動確定は既存の代表選択・Strong・譜面別音源条件を維持します。同数の異なるペアを含む場合も上記の代表選択とStrong条件に従い、同じペアの集合を持つことだけで自動適用を保証しません。利用者が有効な宛先を指定する通常の手動導入にはStrongを要求しません。

候補プロフィールは実在最頻ペアと、正規化キーが一意な全実在ペアを一つの不変値で持ちます。各ペアにはパス順・入力順で選ぶ同一レコードの小文字化前TITLE/ARTISTを対応付け、比較と表示を同じ構築・失効へ揃えます。初回構築は既存キャッシュで共有し、失敗を空プロフィールとして保存しません。対象との分類結果はフォルダだけをキーにした長寿命キャッシュへ保存しません。

`INSTL DST TITLE` / `INSTL DST ARTIST` は次の一組を表示します。空フィールドを代表値で補わず、対象自身の文字列で置換しません。

| 分類 | 候補由来の表示 |
| --- | --- |
| Strong | Strong判定に使う候補最頻ペア |
| Related | Related成立の根拠として選んだ候補の実在ペア |
| Unrelatedを明示保持 | 候補最頻ペア。非関連の警告を区別する |
| Unavailableを明示保持 | 有効な候補最頻ペアがあればその表記、なければ空欄。判定不能の警告を区別する |

導入先が未設定でも先頭の表示候補のペアを対象群へ一括投影します。ドロップダウン選択・候補外の有効な手入力も、既存の対象群と所持未所持分割に合わせて同じ分類器で一度解決します。パスと表示ペアの反映対象がパッケージ全項目でも、比較対象は混在なら未所持群、全既所持なら全対象に揃えます。手入力にStrongを要求しません。所持プロフィールがなければ両列は空です。

空編集は導入先とその表示だけを空にし、候補と推定警告を保持します。明示クリア・再推定・導入後は既存の状態置換に従って古い表示を消去します。detached項目・保存復元では確定済み表示を項目状態として引き継ぎ、フォルダ代表へ戻したり復元時に再探索したりしません。カタログ変更後に既存経路で表示を明示再評価する場合は、失効後の新しいプロフィールを共通分類器へ渡します。

手動導入先は実在し、所持ライブラリに既知のdirectoryを受け付けます。ドライブルートの直接入力（例 `D:\`）も同じルート直下の実fileの親と同じ宛先へ確定し、drive-relativeな `D:` へ変換しません。通常directoryの末尾separatorの有無は同義です。既存の実在・所持検査とUNCの受理範囲は維持し、非所持先を新たに許可しません。この確定は現行パッケージ項目の更新であり、新しいDB保存項目や物理導入を伴いません。

構成ファイルフルスキャンの全件・欠落・欠落無視では、通常所持行の候補選択と手入力を受け付け、編集した一譜面だけを既存の一時投影へ保持します。形式・hash・pathが同じ場合の同一起動中の再表示と、後続の明示再導入要求はその導入先を使います。セル確定だけでは物理処理せず、全再初期化の通知で一時値を消去し、同じDBから作った新library/ownerや再起動へ持ち越しません。別path・別hashや同folderの他譜面へ流用せず、新しいDB列を設けません。

編集要求は保留の `PackageChartEntry` と所持 `ChartFile` の出自を保ちます。同pathが所持と保留に共存しても、所持要求は現在所持の照合後に一譜面、保留要求は現保留群の全項目へ適用します。別instanceの同一対象を従来どおり照合し、捕捉後に保留が退役した要求を所持へ読み替えません。表示でも、所持一時値のproviderを保留の直接行・仮想元集合へ接続しません。保留は項目自身の `Chart`・`ProjectionVersion`・通知を正本とし、所持辞書は導入先・代表値・候補・推定warningだけを扱います。健康度・その他warning・score・chart-info・再生の既存providerは維持します。

保留の明示クリアでは、健康度とリソース警告を登録時に計算した元配置の結果へ戻します。クリアのための再解析・列挙・物理存在確認は行わず、登録後の外部ファイル変更を反映しません。元結果の確定・継承・破棄と非健康度情報の保持は[保留の配置評価](warnings.md#リソース状態の評価)を正本とします。

表示再照合はパス・健康度の変更前に、同じ群の取得済み要求と同梱を捕捉してRelated条件を選びます。パッケージの現在パスに対応する既存入力面cacheを非構築で読み、未取得なら全比較項目で共有している不変の配置文脈を使います。異なる捕捉入力を先頭一項目で代表しません。候補なしの現在配置・detached項目・要求snapshotでも、推定で取得した同梱と完全性を保持します。未捕捉の手入力は確認不能のAnyFieldとして受理し、表示のための追加走査は行いません。源側80%が選択先込みで100%になってもAnyFieldのままです。入力失効後の明示推定は新しい捕捉値で判定し、前回規則・率を別保存しません。

### メタデータの強一致と同順位の解決

共通分類器は分類と候補側の表示根拠ペア、TITLE/ARTISTの類似度を同じ計算から返します。類似度はUTF-16コード単位の重複回数付き文字2-gram Diceです。非空の同一文字列は1、空文字との類似度は0とし、閾値判定の前に丸めません。

1. 対象最頻ペアまたは候補プロフィールに有効な非空TITLEがなければUnavailable。
2. 対象と候補の最頻ペア同士で、TITLE Diceが0.72以上かつ正規化ARTISTが完全一致すればStrong。ARTIST空文字同士も同値です。
3. Strongでなければ候補の全ユニーク実在ペアを調べる。AnyFieldはTITLE DiceまたはARTIST Diceが0.50以上、BothFieldsは同じペアの両Diceが0.50以上ならRelated。
4. 有効な比較材料があり成立ペアがなければUnrelated。

最頻以外に完全一致ペアがあってもRelated止まりです。空TITLEからARTISTだけの新しいプロフィールを作りません。Relatedの根拠が複数なら、採用した規則で成立するペアだけから、`max(TITLE Dice, ARTIST Dice)`、TITLE Dice、ARTIST Diceの降順、次に表示レコードのパスの `OrdinalIgnoreCase` 順と入力順で一組を選びます。別レコードの最大TITLEと最大ARTISTを合成せず、この微差を候補フォルダ同士の順位・確信度へ加えません。

### 祖先候補の抑制

分類・通常モードの非関連除外後に、残った親子関係のある候補だけを調べます。子孫が同等以上の順位で関連性も祖先以上、祖先自身の一致0件、子孫自身の一致1件以上なら祖先を抑制します。保持モードでも関連性の弱い子孫で祖先を消しません。自身の所有分は必要な親子関係だけを照合します。

### 確信度と画面への適用

自動設定対象は採用モードの先頭候補です。先頭がStrong以外なら自動設定せず、下位Strongへの乗換えもしません。先頭Strongに対し、全候補中のStrong同士で主要リソース指標と許可された主ハッシュ数が同じなら `Low / AmbiguousCandidates` です。Jaccard・パスだけで曖昧性を解消しません。Relatedや非関連候補との同率だけで唯一Strongの確信度を下げません。

`AutoApplyAmbiguousInstallDestination` は既存の対象入口でのみ、Strongと音源許可条件を満たす先頭をLowのまま自動設定できます。再導入先修正では無関係候補を除外し、Strongが唯一ならRelatedが併存しても、候補単独で現在配置より主充足率が改善し、音源許可条件を満たせば自動設定できます。複数Strongはリソース順位差や曖昧自動適用設定によらず自動設定しません。同梱は使いません。

音源自動設定の最低充足率は `InstallDestinationAutoApplyMinAudioHealth` で90～100%、1%刻み、既定100%です。旧設定にキーがなければ100%、範囲外の保存値は診断付きで自動設定を拒否し、黙って丸めません。一操作の設定スナップショットへ捕捉します。

候補順位は要求和集合で求めますが、自動設定の音源条件は評価対象群の各譜面で判定します。捕捉済みの同梱・候補索引を使い、使用参照が取得済み、照合不能・未確認がなく、`100×存在音源数 >= 設定閾値×要求音源数` を全譜面で要求します。取得済みの音源要求0件は満たし、未取得を0件へ置き換えません。集約99%でも一譜面が50%なら拒否します。選択候補だけを評価し、全候補×全譜面の物理検査は行いません。

`Complete`・`Missing`・`Unverifiable`・`NotEvaluated` は事実を表します。97%を許可してもMissingをCompleteへ変えず、一般の音源欠損警告を残します。見送り警告は閾値未達・確認不能を区別し、自動設定できたMissingに見送り済みと表示しません。観測済み資源で全音源が揃う場合、入力元の不完全列挙フラグだけでは拒否しません。解析失敗・不正パス・未確認を許容欠損へ変換しません。

所持ハッシュの直接候補にも同じStrongと各譜面音源条件を適用します。順位・分類・確信度・音源状態・自動設定許可は別々の結果です。先頭が閾値未達でも別候補へ探索し直しません。この閾値は推定開始、新規導入分類、`KeepInstallablePackagesPending`、`RequireCompleteUsedResourcesForAutoInstall`、実際のファイル変更の許可条件へ流用しません。

`INSTL DST` は導入先があり `ShouldAutoApplyDestination=true` の場合だけ自動設定します。候補・表示ペア・警告も同じ結果から更新し、生成済みの一覧行へ一操作の完了通知で反映します。手動のパス検証、空編集と明示クリアの区別、保存済み確定値の引継ぎを低層setterで再検査しません。保留の健康度と欠損警告は[配置評価](warnings.md#リソース状態の評価)に従い、各項目へ反映します。

### 自動推定と並列処理

新規取り込みから自動推定を開始するのは、保留対象のうち未所持譜面を含み、同じ操作の初期分類で新規インストール対象ではないパッケージだけです。全既所持や、保留維持設定・ライブラリ未準備などで保留へ残した新規対象は、推定用の資源取得・集約、入力元列挙、ハッシュ候補評価とwork itemを準備前に省略し、`INSTL DST`を設定しません。初期分類の譜面読取り・不足確認、警告と保留DB登録は行い、対象外の兄弟があっても他の対象を処理します。所持分割には表示警告ではなく同じ操作の権威ある所持索引を使い、同操作の自動導入成功も反映します。

自動推定の対象で、使用音源に欠損または定義済み参照の照合不能があれば一括処理の単位で新規リソース探索を行います。入力元の音声充足率が自動設定閾値を満たしていても、100%未満なら必要な自動推定を省略しません。使用音源が完全で既所持ハッシュの候補がない場合は、リソースから新たに候補を探す自動処理を開始しません。非音声だけの不足を理由に厳格設定で保留になった場合も、この新規探索を省略します。既所持ハッシュで一意・単独最多または同点の候補が分かる混在パッケージは、完全音源でも既存の候補集合とStrong評価を残します。未所持群だけを評価し、Strongと各譜面の音源許可条件が成立すれば導入先を設定します。Strong不成立では自動設定せず、表示候補と表示ペアは共通の関連性ポリシーに従います。候補選択の成功を失敗へ置き換えず、候補外へ再探索しません。手動推定は全既所持や新規相当でも実行でき、全既所持時は全対象を評価します。自動新規導入の分類は[設定仕様](../runtime/settings.md#自動新規導入に要求するリソース)に従います。起動時に復元した保留は自動推定せず、利用者の明示的な手動推定で処理します。

一件の手動推定では候補評価を並列化し、`asParallel=true` の並列数は `max(1, CPU数 - 1)`、falseなら1です。複数パッケージの一括推定は外側を並列化し、各候補評価は1にして二重並列を避けます。自動推定の外側並列数は `PendingInstallEstimateMaxParallelPackages`、0ならCPU数から決めます。手動の複数パッケージも同じ一括経路を使います。

全選択譜面が保留パッケージに属する場合はパッケージ単位へまとめられます。単独譜面が混じる場合と再導入先修正は外側を逐次実行し、内側の候補を並列評価します。取り込みで取得した参照集合は同じ対象の評価へ再利用します。

取り込みから推定・[自動導入](#取り込み後の自動推定に続く導入)への直接継続と受付終端は[競合ポリシー](../core/operation-concurrency-policy.md#ライブラリ保留保守)に従います。物理変更leaseの解放を推定完了と扱わず、確定済み登録・適用済み結果・未適用の保留を実結果へ区別します。

### 推定入力の確定と停止

受理後に現在対象・所持未所持の分割・評価入力と設定を一度捕捉します。パッケージとloose譜面が混在する手動要求、複数譜面の再導入修正でも、一回の設定スナップショットを全対象へ渡します。取り込みが捕捉した設定は直接推定の並列度と評価にも使います。準備した入力元のリソース参照集合と分割は同じ操作内で再利用します。譜面情報の読込み・ハッシュと譜面情報の補完・所持保守・LR2同期など、推定入力を実際に更新する受理済み背景処理も同じ論理受付を使います。背景処理は先行操作の実終端を非同期で待ち、Busyで捨てません。通信・表示キャッシュ・リソース健全性の読取り表示は、それだけを理由に全体待機へ含めません。独立キャッシュと所持集合の版、通知順、再接続の識別、物理変更権限、リソース健全性の入力保護は各責務へ残します。

取消または最初の評価例外を観測した後は、新しい評価のdispatchと結果のapplyを止めます。開始済み兄弟が後で正常終了しても未反映結果を適用しません。既に適用した推定と確定済み登録・導入は保持し、未適用対象を次の明示推定用に保留へ残します。開始済み全評価Taskと後片付けの終端までBusyを保ち、全対象のSEARCHINGを解除してから受付を解放します。結果には確定済み事実と実際の準備・評価の失敗・取消を保持し、失敗を空の成功結果へ置き換えません。

### 検出した物理範囲の保持

通常検出で単体ファイルとなったパッケージは、同じ親にある兄弟の推定先や既所持先が一致しても、親ディレクトリへまとめ直しません。通常完了・登録後取消・推定対象0件のいずれも、初期のパッケージ単位と保留DB行を保持します。警告・同梱範囲を親へ広げず、別保留の子パッケージを導入へ巻き込みません。

ファイルを直接指定した場合の実在する隣接資源によるフォルダ化と、folder／全項目選択・子作品の分類は[ドロップ導入](drop-install.md#単体分割と子作品の発見)に従います。初期からdirectory型として捕捉した範囲は、未参照の付属物も相対位置ごと導入します。

譜面を含む全項目選択を展開前に親directory入力へまとめた場合も、内包書庫を通常同梱として保持します。一式の配置は通常の手動導入でも同じであり、有効な所持先の明示指定に自動推定のStrongや音源充足を要求しません。

同じ物理範囲のA/B単体を先に登録しても、後のfolder／全項目選択で完全に取得した新directoryへ登録置換できます。新directoryと旧群の対応は一要求の準備結果だけに持ち、採用された群の旧削除と新追加を既存sessionで一括確定します。読取り不完全・取消・未採用・DB失敗では当該旧群を先行変更しません。不完全scope内の旧登録をmissing整理で解除せず、存在する無関係保留は保持します。単位変更で旧DST・候補・配置健康度をコピーせず、同path・同内容の確定文字コードと対応メタデータだけを新しい読取りへ引き継ぎます。別物理rootの由来識別、起動復元と選択意図なしの過去overlap整理は含めません。

登録置換は既存sessionの登録と状態反映として行い、物理処理は通常の導入に従います。未採用・取消群では旧削除と新追加を双方除外し、既に物理成功した独立対象は既存の成功保持に従います。別入力で既に存在しない保留を整理する従来の扱いは、当該scopeの不完全な読取りとは区別します。

### 推定先への実導入

短いモデルの保護範囲で現在の保留と照合し、入力順に重複を除き、有効な項目の導入先が全て空の候補を除きます。保護範囲を出てから適格な候補だけを捕捉し、除外した入力・行・導入先・警告は変更しません。

分類と物理処理は入力順です。所持判定には開始時の主ハッシュと `SessionSuccessOwnershipOverlay` の先行成功を使います。同一パッケージ内の同じハッシュを重複処理せず、既所持を除いた新規対象の導入先が空または複数なら保留を維持します。

実導入は一つの変更セッションへ集約します。全譜面が既所持でも、リソースだけ・後片付けだけの必要な変更は残します。追加BMS・BMSONと、リソースの移動先にある既所持譜面を末尾の保守対象にします。何も移動しない後片付けだけの成功では保守しません。

物理完了時のパス・導入状態の反映と、保守から所属登録への順序を維持します。必須後処理が全て成功した後、今回導入した譜面の同じ所持tokenから現在値を取り直し、加入済みentryへ計算済みの充足値を局所反映します。既存の延期通知をリース解放後に公開し、失敗・取消時に成功した後処理を装いません。

既存のリソース状態索引が有効なら対象だけを差分更新し、索引が未構築・無効なら、この後処理だけを理由に全体再構築しません。入力元の保全、確認済み残存物の削除、終端の報告は[ファイルとDBの整合](file-db-consistency.md)に従います。

### 取り込み後の自動推定に続く導入

`AutoInstallAfterAutomaticEstimation` が有効な取り込みでは、保留への登録に続く自動推定が正常完了した後、今回導入先を自動設定した対象を既存の推定先導入核へ渡します。設定の既定値・保存・取消は[設定仕様](../runtime/settings.md#取り込み後の自動推定に続く導入)に従います。ドロップ・ファイル指定・ダウンロード由来で同じ取り込み経路へ入る入力へ適用します。

手動の推定・再推定、候補選択、導入先の手入力・修正、起動時の保留復元、設定の有効化だけでは導入を開始しません。候補が存在するだけの対象や、以前から導入先が設定済みの別保留も含めません。自動推定の資格、音源充足率、Strong判定、曖昧候補の自動設定許可は既存の判定を再利用します。新規判定の保留維持設定を本機能の全体停止条件へ転用せず、推定対象でない入力へ推定を追加しません。

#### 今回の対象と物理範囲

取り込みに続く `AutoInstall` sourceの自動推定で、現在の保留・有効なentryの検査を通り、`ShouldAutoApplyDestination` と導入先が成立して実適用が完了したentry・導入先だけを、一操作の間収集します。設定無効時には後段用の追跡を行いません。永続的な起源フラグや全保留の監視は追加しません。

初期検出した単位を保つ現在の保留パッケージから、次の全条件を満たす有限集合を確定します。

- 今回自動設定した有効なentryを一つ以上含む。
- 導入対象の未所持entryが、すべて今回の自動設定に由来する。
- 現在の導入先が今回自動設定した先と一致し、既存の推定先導入条件を満たす。

古いパッケージ、親ディレクトリ、ハッシュだけから候補を拾い直しません。同じ元ディレクトリの単体譜面は同じ宛先でも単体のまま一つの導入セッションへ渡します。別保留の子や今回の自動設定外の対象はその入力・候補・警告・保留行を維持します。初期ディレクトリパッケージ内に今回の自動設定外の未所持entryが一つでも混ざれば、そのパッケージ全体を保留に残します。推定対象が0件の場合、後段候補は空です。

候補の資格は後段開始前に確定します。同じバッチの先行成功によって後続が既所持になった場合は、既存核のリソースのみの導入・片付けのみの処理を維持し、候補を独自に除外し直しません。

#### 実行と取消

初段の通常導入・保留登録を確定してから推定を待ちます。推定の失敗・取消では、適用済み推定を保持しても後段を一件も開始しません。正常完了・設定有効・未取消の場合だけ、候補を一つの推定先導入バッチへ渡します。初段と後段の変更セッションは別で、後段をパッケージごとの確定へ分割しません。

同じ取り込みのcapabilityと入口で捕捉済みの `BmsLibraryOptionsSnapshot` を、物理scope準備、上書き、元削除、導入、必要同期まで明示的に渡します。手動導入も自分の受付で捕捉した設定を同じ核へ渡します。物理変更の受付と停止順は[競合ポリシー](../core/operation-concurrency-policy.md#ライブラリ保留保守)に従います。元の保留入力・管理ZIP・借用ZIPの寿命は[ドロップ導入](drop-install.md#導入後の入力の寿命)に従います。

自動導入は差分導入の確認を省略します。物理処理・SmartOverwrite・元削除・所持集合・DB・索引・導入済み表示・導入先消去は既存核を使います。手動導入の確認は変更しません。後段開始前の取消では後段を開始せず、後段実行中は現在のパッケージを最後まで処理して次を開始しません。成功分を確定・反映し、未開始対象は元と推定先を保持した保留へ残します。物理失敗と型衝突は既存核の継続・停止条件に従います。

#### 終端結果と同期

初段・後段のreceiptを区別し、それぞれの永続確定と必須反映の成否を保持します。初段成功後の推定・準備・停止・後段・同期・後片付けの失敗でも、先行の確定を空結果へ置換しません。必須反映に失敗した段階は、永続確定済みでも導入済み表示の成功として公開しません。同期可能な確定分は[LR2同期](../integration/lr2-song-db.md#局所変更と性能)に従い操作末尾の一回へ集約し、元の失敗・取消も保持します。

ownerから完了・失敗receiverまで、成功として公開できる初段・後段の導入済み対象を重複なく集約し、競合・取消による後段の未実行対象、物理失敗・型衝突・回復パス、必須反映・セッション外の失敗を一回の終端報告へ渡します。通常の保留件数と後段未実行件数は区別します。必須の公開・入力回収を待ち、[未引渡し管理入力の回収失敗](drop-install.md#導入後の入力の寿命)も同じ結果へ反映します。任意の終端案内は受付解放後に配送します。案内の表示失敗で確定済み変更の結果を変えません。

### 診断

`estimate_install` でモード、要求数、同梱で満たす数、逆引き訪問数、リソース条件通過数、全候補の分類件数、実際のメタデータ比較ペア数と区間時間を確認します。プロフィール構築・再利用・入力行数の累積値は、その一推定だけの件数と混同しません。上位候補はHの元件数、候補自身M、Jaccard、分類と表示根拠、音源閾値・拒否理由を記録し、全候補の全文は通常ログへ出しません。一括の `packageDegree` と候補内並列度、初回・再利用の費用も分けます。

源側の観測一致数・要求数・状態、Relatedのand/orと採用理由も一推定の結果へ含めます。率確認済み・観測下限・要求なし・確認不能・再導入対象外を区別し、未確認を0%や100%に見せません。候補が残らなくても評価済み入力の理由を残します。源側計数は既存sAudioと共有する一推定O(|R|)で、ワーカーごと・候補ごとに再計数せず、プロフィールも規則別に構築しません。

性能計測は[明示性能検査](../development/testing.md#解析大規模データ外部エンコーダー)へ分離し、恒久的な機能テストに時間・割当・仕事量の合否を含めません。要求100・300・500・1,000、同梱100%・90%・89%・80%・なし、数千・2万超候補、複数・1,000級ペア、単体・20入力、初回・再利用の代表組合せを再現可能な合成入力で測定します。条件切り替えの比較は基準版と同じ入力を使い、源側評価・メタデータ分類・結果反映を分けます。反復の全値・中央値・範囲と測定した完了範囲を示し、事前のC++/Python実験値を製品実測として扱いません。

実導入の対象別記録は物理処理の単位であり、DB確定回数ではありません。`reverse_lookup_incremental_update`、`maintenance_update`、`resource_health_index_delta`、`chart_info_inline_install` で操作末尾の集約を確認します。

### 保留パッケージを新規として導入する確認

「新規としてインストール」は設定・推定された導入先を使用せず、新規インストール先へ導入します。推定、候補生成、リソース警告、導入処理自体は変更しません。

パッケージ内に導入先設定済みの譜面があれば、設定先を使わない旨を必ず確認します。全譜面の導入先が未設定の場合は、`ShowNewPackageInstallConfirmMsg`（既定 true）に従い、保留を意図的に新規扱いし、リソース不足等の WARNING が残っていても導入する旨を確認します。この設定を false にしても設定済み導入先の確認は省略しません。

確認は共通論理受付の取得後、モデルの物理変更リース取得前に解決し、既定の応答を No にします。ボタンを選ばず閉じた場合も承認せず、拒否されたパッケージは導入先の有無によらず変更せず、入力元・DB・保留を保持します。複数選択では承認されたパッケージだけを既存の一括導入経路へ渡します。

## 実装とテストの対応

[`MainWindowPackageMaintenanceWpfTests.PendingInstallBar_UsesCurrentBoundSelectionAndPreservesMultipleRowsAfterFocusRequest`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPendingInstallBarWpfTests.cs) は実Button.Clickから `RelatedOnly`・選択譜面用 `ManualInstall`・保留解除への配送と実変更活動中の拒否、完了後の実view terminalによる選択解除とツリーplanを確認します。既存の `CompiledSelectedChartRoutesPreservePendingTargetAndHandledBeforeCompletion` はメニューの `KeepUnrelated`→通常推定・新規導入・配置先修復・元entry/tokenと完了前Handledを維持します。実推定/解除の行反映、receipt、導入本体の保証は既存の下表へ分担します。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 手動導入先の所持/保留出自、表示分離と一時保持 | `GridRowResolver`、`BmsLibraryPendingPackageStore.SetPending`、`BMSLibrary.SetPendingInstallDestination`、`MainChartRowProjectionOwner` | `BmsLibraryPendingPackageRegroupTests.SetPendingDestination_OriginSeparatesOwnedChartFromSamePathPendingGroup` はBMS/BMSONの同path共存・所持一譜面/保留全項目・失効保留拒否と新所持要求を実storeから確認する。既存空編集/clear/validator/健康度系列を維持する。`ChartListVirtualViewTests.PackageChartSourceRows_InstallDestinationEditsNotifyCurrentProjectionAndPreserveEstimation` は直接package行とvirtual sourceの両方を、同path所持一時値から分離し、項目更新後の自身の表示値・次の保留要求・所持修復要求の値を確認する。`RegularChartNavigationTests.InstallDestinationEdit_SurvivesRedisplayButNotFullReinitializeOrNewOwner` は実編集・通常再表示・実FullReinitialize通知と同DB新ownerの非保持を確認する。`MainWindowPackageMaintenanceWpfTests.FullScanDestinationEditing_RealTreePreservesOriginAndFeedsExplicitRepair` は三つの実treeからのcompiled候補/手入力/取消・再表示・後続修復要求、代表一条件の同path保留との相互非汚染と元file消失/先bytes/所持path/fresh DBを確認する。 |
| 子譜面packageのルート要求、同梱と候補の足切り・順位 | `ChartResourceSnapshot.CreatePlacedAggregate`、`PackageInstallEstimationSnapshotBuilder`、`BMSLibrary` | `ChartResourceSnapshotTests.CreatePlacedAggregate_ProjectsIndexKeysAndPreservesOriginalReferences` が全種類の配置付きkeyと原参照・状態を確認する。`BmsLibraryInstallEstimationServiceTests.Estimate_NestedPackageUsesRootCoordinatesBeforeCandidateGate` は実builderと逆引きからroot/子の二要求、誤った親・兄弟の除外を、`Estimate_NestedPackageRanksUnionHealthBeforeDuplicateMatches` は正しい子同梱とH/U/Mによる順位を確認する。`BmsLibraryPendingPackageRegroupTests.SelectDestination_UsesCapturedSourceRuleAcrossEditingAndExplicitReestimation` は子配置の8/10・9/10、編集時の捕捉保持、混在AnyDSTの全体skipと全DST明示clear後の再推定を分担する。 |
| 全hash未所持のroot＋子packageの通常発見から共有事前集約・実導入 | `BMSLibrary.BuildAutomaticPendingEstimateSourceBatchSnapshotUnsafe`、`PackageInstallWorkflowOwner` | `PackageInstallWorkflowOwnerTests.AutomaticContinuation_NestedUnownedPackageUsesPlacedResourceRequests` が親だけの通常入力から両譜面を発見し、hash短絡を使わずroot同梱＋子候補で推定する。最終階層・元bytes、song/所持集合・保留/install除去・DurableCommitを確認する。既存のhash候補・子音源不足の系列と停止/取消/全Task joinを維持する。 |
| 同一推定対象群の確定DST混在時は既存skipを維持、全DST明示クリア終端後に再推定 | `PendingPackageWorkflowOwner.SearchPackagesAsync` / `ClearPackagesAsync`、`BmsLibraryPendingPackageStore`、`BMSLibrary.PackageInstall` | [`PendingPackageWorkflowOwnerTests.SearchPackagesAsync_PartiallyConfiguredPackageRequiresExplicitClearBeforeReestimation`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) が実owner/storeで、旧Manual DST保持と空項目不変、全クリア後の両項目の元配置0%、次の明示推定で一意Strongの新宛先と100%への到達を区別する。各実Task・Presentation・L終端、同じ二項目・元ファイルとfresh read-only接続による元pathのinstall記録保持も確認する。失効factsと独立パッケージの実統合は[変更操作の対応表](mutations.md#実装とテストの対応)へ分担する。 |
| 推定済み保留候補・配置の成功変更追従と、削除・統合後の明示再推定 | `LibraryInstallDestinationChange`、`PackageChartEntry.ApplyLibraryFolderChange` | 条件表と実成功factsは[変更操作の対応表](mutations.md#実装とテストの対応)へ分担する。`BmsLibraryDuplicateServiceTests.MergeChartDirectory_RechecksResourcesWithinAcceptedOperation` は統合直後の失効と、DST空の独立した単独パッケージ一件を明示推定して現存先から候補を取得する接続を確認する。同一対象群の確定DST混在は前行へ分担する。 |
| 資源拡張子に似た親名の通常候補 | `BmsLibraryInstallEstimationService`、`DirectoryResourceLookupCache` | `BmsLibraryInstallEstimationServiceTests.EstimateInstallationDirectory_DistinguishesAliasLikeParentNamesFromRealReverseIndex` が実走査索引から別親の候補を区別する。`EverythingNativePathIdentityTests.ExecuteScan_PreservesDirectoryIdentityThroughDecodeHealthAndEstimation` はnative公開packの実decodeと三逆引きから通常推定への接続を担う。条件表はnativeへ集約し、既存の閾値・祖先抑制系列を維持する。 |
| 絶対drive rootの候補と子候補との階層 | `BmsLibraryInstallEstimationService`、`DirectoryResourceLookupCache` | `BmsLibraryInstallEstimationServiceTests.EstimateInstallationDirectory_PreservesAbsoluteDriveRootFromRealReverseIndex` は実走査索引から一意なrootを選び、`Estimate_AbsoluteDriveRootParticipatesInAncestorSuppression` は既存関連性規則をroot/子へ適用する。`EverythingNativePathIdentityTests` のroot代表は非root作業場所でnative生成物から通常候補まで接続する。 |
| パッケージ内の子譜面の最終配置における音源充足・保留表示と自動導入 | `PackageInstallEstimationSnapshotBuilder`、`PackageInstallDestinationMap`、`ChartResourcePlacement`、`BmsLibraryInstallEstimationService`、`BmsLibraryPackageInstallService.RefreshPendingResourceHealth` | [`BmsLibraryInstallEstimationServiceTests.Estimate_NestedPackageChartRequiresAudioAtFinalRelativePlacement`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) は候補・同梱それぞれの親だけの音源を拒み、子音源を認める4条件を、実パッケージの推定入力から同じ項目への適用・健康度再評価まで確認する。自動許可と件数・健康度・不足警告の一致、最終子配置、候補と元健康度の保持を確認し、同梱子音源の代表条件ではクリア後の配置文脈も確認する。[`PackageInstallWorkflowOwnerTests.AutomaticContinuation_NestedChartUsesFinalAudioPlacement`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) は実取り込みから、親だけの場合の元FS・保留・install保持、正確な子項目の件数1/0・健康度0・不足警告・候補保持と、子にある場合の階層を保つ導入・song・所持反映を確認する。閾値境界と兄弟譜面の不足条件は既存の推定サービス系列が分担する。 |
| 同物理入力の明示再投入と原子的な登録置換 | `BmsLibraryPackageInstallService`、`AutoInstallWorkflowResult`、`BMSLibrary.PackageInstall` | `PackageInstallWorkflowOwnerTests.ProductionImport_ReplacesSamePhysicalPendingScopeOnlyAfterAcceptedCompleteRead` はBMS/BMSON混在の二回取込みでfolder／全選択への置換、実在無関係保留・旧DST非継承・同内容文字コードを確認する。実readlock・準備後取消・install挿入の実SQLite失敗で旧DB・現在値・入力bytesの保持を確認する。 |
| 初期一式の全同梱と自動適用条件の分離 | `PackageInstallInputSelection`、`BMSLibrary.PackageInstall`、`PendingPackageWorkflowOwner` | [入力単位と内包書庫の通常入口検証、および四譜面・十資源の手動／自動導入](drop-install.md#実装とテストの対応)へ分担する。手動代表は元と異なるmetadata・独立音源の所持先、実保留登録、`SetPendingAsync`、既定実storeの `ManualInstallPackagesAsync` を通す。既存所持と無関係保留を保ち、元ZIP・全相対path/bytes・song/install・durable終端を確認する。自動代表の合成所持前提を手動へ持ち込まない。 |
| 取り込み後の自動推定からの導入、設定捕捉と初期物理範囲 | `BMSLibrary`、`BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService` | [`PackageInstallWorkflowOwnerAutomaticContinuationTests`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) の `AutomaticContinuation_UsesCapturedSettingsAndInitialDirectoryScope` は設定ON/OFF・元削除ON/OFF・推定中の反転と次操作、初期混在フォルダの全内容・相対位置・SQLite・所持集合を確認する。`AutomaticContinuation_PreservesDetectedFileScopesAndChildPending` は同／別宛先×元削除ON/OFFで通常検出・通常推定・後段導入を通し、A/Bだけの導入と子Cの元bytes・path・候補・警告・install列保持、後続明示C導入を確認する。混在分類と設定条件は既存推定系列へ分担する。失敗・取消の保証は以下の後段系列、採番後に残る実同梱資源の型衝突と独立対象継続・未復元の宛先・先行成功と未処理の保持は[整合の対応表](file-db-consistency.md#実装とテストの対応)の既存導入系列へ分担する。 |
| 手動推定・復元・候補編集からの非起動 | `PendingPackageWorkflowOwner`、`BMSLibrary` | `PackageInstallWorkflowOwnerTests.AutomaticContinuation_DoesNotRunAfterManualEstimation` は新設定有効の実モデルの手動推定後も物理導入・保留除去しないことを確認する。手動owner、候補編集・修正・復元は既存の `PendingPackageWorkflowOwnerTests`、`BmsLibraryPendingPackageRegroupTests`、`BmsLibraryPackageLifecycleTests`、`BmsLibraryInitializationInstallTests` が分担する。 |
| 後段の取消・対象別競合・停止と受付寿命 | `BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService`、`PackageInstallWorkflowOwner` | `PackageInstallWorkflowOwnerAutomaticContinuationTests` の `AutomaticContinuation_WaitsForStopAndCancelsOnlyBetweenPackages` は実停止Taskと物理処理の到達点で制御し、停止前の未書込み、現パッケージ完了・次の未開始保留、部分成功と受付終端を確認する。LR2有効行は停止Task中の追加P保持、取消後の必要同期とL/P解放も確認する。`AutomaticContinuation_ExcludesOnlyPlaylistBusyTargetWithoutReplay` は交差Xの未実行と独立A/Bの一括確定、P解放後の非再実行を確認する。推定失敗・取消の全joinは既存の `PackageInstallWorkflowOwnerEstimationTests`、管理・借用入力は既存のarchive系列が分担する。 |
| 二段階の確定前／必須反映失敗と実receiverへの集約表示 | `BMSLibrary.PackageInstall`、`PackageInstallWorkflowOwner`、`MainWindowViewModel`、`FileDbMutationReport` | `PackageInstallWorkflowOwnerTests.AutomaticContinuation_RetainsFirstStageWhenSecondStageTransactionFails` は実DBのsong／maintenance書込み失敗で初段保持・後段全体の確定境界・回復情報を確認する。[`MainWindowPackageMaintenanceWpfTests.DropInstallAutomaticContinuation_ReportsBothStagesThroughActualReceiver`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) は実取り込みowner通知から実receiverと記録dialogへ通し、受付解放後の一回の本文に初段成功・後段未確定・回復情報と、譜面なし管理ZIPの削除失敗の原因／対象パスが届くことを確認する。回収失敗と元原因・取消の分担は[入力回収の対応表](drop-install.md#実装とテストの対応)、表示の既存組合せは `FileDbMutationReportTests` を参照する。 |
| 後段バッチ内の先行成功による後続の既所持化と別の既存保留の除外 | `BmsLibraryPackageInstallService`、`SessionSuccessOwnershipOverlay` | [`PackageInstallWorkflowOwnerTests.AutomaticContinuation_ReclassifiesLaterOwnedCandidateInsideSingleBatch`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) は先の実owner取り込みを設定OFFで行って別入力元の導入先設定済み未所持保留を作り、今回ONの同hash二候補だけを導入・片付けする。一回の後段確定で後続を片付けのみへ分類し直すこと、song・導入済みの重複なし、両方の元片付けと今回二候補の保留除去を実FS/DBで確認する。別保留は元bytes・設定先・hash・install行のpath／delete_parentを維持し、song・所持集合へ追加せず、設定先にファイルを作らないことも確認する。リソース専用の下位判定は既存の `BmsLibraryPackageInstallServiceTests` が分担する。 |
| 譜面発見の完全性と破壊的操作の確認 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | 列挙の成否・完全性と全譜面の読取り結果から完全性を作る範囲は静的に確認する。実際の部分取得・部分管理列から削除への接続は[削除の対応表](mutations.md#実装とテストの対応)の実FS・DB試験、列挙失敗そのものは既存の `RootFileEnumerationTests` に分担する。 |
| 新規としての導入確認、設定による省略、拒否対象の保全 | `PendingPackageWorkflowOwner`、`BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService` | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) の `ForceInstallPackagesAsync_ConfirmsAccordingToDestinationAndNewInstallSetting` は要求の既定値を実表示部品で正規化した `ClosedByUser` 応答が未承認になることと項目保持を確認し、既存の確認条件・混在選択も維持する。[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `ForceInstallPendingPackages_PublicEntryUsesConfirmationSettingAndPreservesRejectedInput`: 公開入口で未設定ONの新規確認、未設定OFFの無確認導入、設定済みOFFの確認維持、No・未選択閉鎖時の入力元・DB・保留保持、肯定時の実新規導入を確認する。閉鎖応答は要求のボタンと既定値を `ThemedMessageBox.NormalizeDefaultResult`・`UiDialogResult.ClosedByUser` の既存変換へ通して生成する。明示的な未承認パッケージ保全の既存ケースも維持する。 |
| 新規としてのメニュー表示と既存操作経路 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `CompiledTreeMenusPreservePackageSectionsAndPendingOperations`、`CompiledSelectedChartRoutesPreservePendingTargetAndHandledBeforeCompletion`: ツリー・選択譜面の実ForceInstall項目Headerと表示リソースの対応、既存経路・対象・完了を確認する。 |
| 新規としての実導入先と設定先の非使用 | `BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta`: 実導入先が設定済みExplicitと異なること、Explicitへ入力譜面・資源を作らないことを、既存の仕事量・通知・永続結果とともに確認する。 |
| 実在ペア・Strong0.72・Related0.50 Any/Both・成立根拠一組・正規化 | [`InstallEstimationMetadataProfile`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/InstallEstimationMetadataProfile.cs) | [`InstallEstimationMetadataNormalizerTests`](../../../BeMusicSeeker.Tests/Install/InstallEstimationMetadataNormalizerTests.cs) が両規則の境界、同一ペア、空値、非最頻完全一致、安定した一組、末尾1,000ペア、NFKC・役割・区切り・UTF-16・二重適用防止を確認する。 |
| 源側90%境界・不明と0件・部分下限・入口と設定の独立性 | `PackageInstallEstimationSnapshot`、`BmsLibraryInstallEstimationService` | `BmsLibraryInstallEstimationServiceTests` が89/90%と90/101の整数境界、観測数と完全性、通常/限定/hash/並列、再導入・保持モード・各譜面許可との独立性を確認する。実入力のバッチ伝播は `PackageInstallWorkflowOwnerTests` と `BmsLibraryPendingPackageRegroupTests` が分担する。 |
| 候補編集時の源側条件・表示根拠維持、非構築読取り、detached | `BMSLibrary`、`ChartPackage`、`ChartResourcePlacement`、`PackageChartEntry` | `BmsLibraryPendingPackageRegroupTests` が源側80%/90%以上の実推定と候補選択・空編集・手入力、捕捉入力の保持と明示再推定を確認する。非構築APIから列挙への到達がないこととsAudio共有は静的に確認する。 |
| ARTISTの副役割除去・境界・括弧・制作担当の保持再開・名義の保護と集約 | `InstallEstimationMetadataNormalizer` | `InstallEstimationMetadataNormalizerTests` の `IsStrongMatch_IgnoresSecondaryArtistCredits`、`IsStrongMatch_PreservesMainArtistNames`、`IsStrongMatch_ResumesAtExplicitMusicCredits`、`BuildProfile_GroupsEquivalentArtistCredits`。両側へのサービス接続は `BmsLibraryInstallEstimationServiceTests.EstimateInstallationDirectory_StrongRequiresPairArtistAndTitleThreshold`、実BMS解析からStrong・自動適用への接続は `EstimateInstallationDirectory_ParsedBmsSubrolesRemainStrongAndAutoApply`、整理後の候補表示は 候補の表示ペアを確認する系列。 |
| 通常と保持の全候補順位・表示3件・Strong間曖昧性・祖先抑制 | `BmsLibraryInstallEstimationService` | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) が関連性優先と資源優先、11位以降・表示外競合、Jaccardだけでは解消しないこと、弱い子孫で親を消さないことを確認する。 |
| 逆引きによるs/M/D/U・80%以上・カテゴリと相対キー | `BmsLibraryInstallEstimationService`、`DirectoryResourceLookupCache` | `BmsLibraryInstallEstimationServiceTests` が単純な集合演算との一致、同梱補完・1件境界・重複を、[`DirectoryResourceLookupCacheTests`](../../../BeMusicSeeker.Tests/Resources/DirectoryResourceLookupCacheTests.cs) が逆引き更新・世代を確認する。 |
| ハッシュ解決事実と通常関連性フィルタ・各譜面音源許可 | `BmsLibraryInstallEstimationService`、`ChartResourcePlacement`、`BMSLibrary` | `BmsLibraryInstallEstimationServiceTests` が直接候補への入口80%非適用、90/97/100%・不正値・各譜面判定・既知0/未取得0・Missing保持と見送り理由、拒否後の乗換え禁止を確認する。 |
| ハッシュ経路の集約単位・全既所持の通常推定・拒否後停止・一括結果反映 | `BMSLibrary`、`BMSLibrary.PackageInstall` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) で未所持群または全既所持群の代表、単独bmson、最多候補拒否後の迂回防止、候補・警告・SEARCHING・進捗終端を確認する。 |
| 自動新規導入の設定と音源不足時の自動推定 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs)、`BMSLibrary.PackageInstall` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) で設定ON/OFF・音源／非音声不足・未解決・補助画像・他の保留理由を確認する。同一操作の設定捕捉、自動設定閾値以上でも100%未満なら推定する接続を代表ケースで確認する。`PackageInstallWorkflowOwnerTests.ProductionImport_UsesCapturedCompletenessPolicyAndEstimatesMissingAudio` はハッシュ根拠なし完全音源でwork itemを開始しないことと手動推定を分担し、`ProductionImport_CompleteSourceAudioStillEvaluatesKnownHashCandidates` は実混在取り込みで一意先Strong成立・拒否と同点限定集合を維持し、新設定有効でも曖昧自動適用が無効なら候補だけを残して保留することを確認する。 |
| 自動対象の準備前選別と手動推定 | `BmsLibraryPackageInstallService.ApplyAutoInstallWorkflowForMutationSession`、`BMSLibrary.BuildAutomaticPendingEstimateSourceBatchSnapshotUnsafe` | [`PackageInstallWorkflowOwnerTests.ProductionImport_SkipsOwnedAndNewPackagesBeforePreparationButEstimatesMixedSibling`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) は同じ全既所持・設定保留の新規・混在入力を新設定OFF/ONで取り込み、前二者の準備・評価なし、元bytes保持・song追加なしを確認する。混在だけを一回準備・評価し、OFFは3保留と未所持の推定先設定、ONは実FS・song・所持集合・導入済みへの反映と混在のinstall行除去を確認する。両行でSEARCHING解除・受付解放を確認し、ONでも後続の新規対象の手動推定から導入しない。除外対象の迂回防止と同操作の先行成功索引の利用は静的確認も併用し、全既所持の手動個別・一括系列は `BmsLibraryPendingPackageRegroupTests` が分担する。 |
| 混在取り込みのハッシュ候補拒否と保留確定 | `BMSLibrary.PackageInstall`、`BMSLibrary` | `BmsLibraryPackageInstallServiceTests.AutoInstall_MixedFolderHashMetadataMismatchKeepsPendingInputAndCandidate` は実フォルダ入力、実DB保留行、未所持入力・候補・表示・警告保持と受付解放を確認する。 |
| Strong不成立の候補・WARNINGと生成済み行への投影 | `BMSLibrary`、`PackageChartEntry`、`LibraryChartRow` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) の `SearchEstimatedInstallationDirectory_MetadataMismatchLeavesInstallDestinationEmptyAndShowsSingleSuggestion` と二候補の既存ケースが実索引・実BMS入力から不一致候補を確認する。[`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `PackageChartEntryDisplayRow_ReflectsUpdatedAdapterlessBmsonEntryState` は生成済み行へのMetadataMismatch・代表表示・候補・adapter非生成を確認する。判断の条件網羅はサービス側へ分担する。 |
| BMS・bmsonの原題入力、形式別の作者入力、空値からの非補完 | `PackageInstallEstimationSnapshotBuilder`、`BMSLibrary`、[`BmsonChartFileParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsonChartFileParser.cs) | `BmsLibraryInstallEstimationServiceTests.PackageInstallEstimationSnapshotBuilder_BuildsTargetMetadataProfileFromFormatSpecificMetadata` は対象側の原題・作者を形式別に確認する。所持側は `BmsLibraryPendingPackageRegroupTests` の `SetPendingInstallDestination_UpdatesRepresentativeMetadata` と `SearchEstimatedInstallationDirectory_ResolvesBmsonFromInstalledBmsonHash` が、未知のSUBARTISTを含むBMS・合成Artistを持つbmson・空Artist・現在値更新後の代表表示を確認する。[`BmsonSongParserTests`](../../../BeMusicSeeker.Tests/ChartInfo/BmsonSongParserTests.cs) の `Parse_MissingTitleKeepsRawTitleEmptyAndComposedDisplay` は原題の省略・null・空文字を確認し、失効後の再推定は `SearchEstimatedInstallationDirectory_MetadataTieBreakSelectsMatchingCandidate` でも維持する。 |
| 相対キー、カテゴリ、候補抽出、順位と確信度 | [`BmsLibraryInstallEstimationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs)、[`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) |
| 入力元の走査上限、単一ファイルとディレクトリの区別 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) の `PackageInstallEstimationSnapshotBuilder_DirectoryPackageDiscardsPartialSurfaceAtLimit` は上限超過で資源を破棄すること、単一ファイル・単独BMSONの既存ケースは親を走査しないことを確認する。[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) は実導入との接続を確認する。 |
| 取得済み部分資源の受入と消費側の診断 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | 通常失敗の固定列挙結果を実builderへ渡し、直接・共有入力から通常・ハッシュ推定への観測済み資源と完全性の受渡し、全音源充足時の自動確定、未観測参照の確認不能理由を `BmsLibraryInstallEstimationServiceTests` で確認する。全種別の健康度・不足警告と配置文脈は `BmsLibraryPackageInstallServiceTests` で分担する。成否・件数・理由の診断接続は静的に確認する。列挙自体の失敗と独立ルート継続は[パスとI/Oの対応表](../core/path-length-and-io.md#実装とテストの対応)で分担する。 |
| 起動復元の手動推定 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) の `Initialize` | [`BmsLibraryInitializationInstallTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationInstallTests.cs) の `Initialize_RestoresPendingWithoutAutomaticEstimationAndAllowsManualEstimation`: 実DB復元と必須背景更新の終端まで評価を開始せず、復元対象の明示推定で導入先を設定できることを確認する。 |
| 取り込みから直接推定への受付、取消・例外後のdispatch/apply停止と全Task終端 | [`PackageInstallWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PackageInstallWorkflowOwner.cs)、`BMSLibrary.PackageInstall`、`BMSLibrary` | [`PackageInstallWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) の `ProductionImport_StopsDispatchAndApplyThenJoinsSuccessfulSiblingBeforeAdmissionRelease`: 管理実ZIPを含む本番mutation portと実DBの保留確定、外側並列度2、A適用済み・B/C成功評価保持・D未開始から取消と例外を確認する。停止後にCを成功終端させても未適用、D開始0、全Task回収までBusy、SEARCHING全解除、確定事実・未適用管理入力と元の失敗保持、次の明示要求成功を確認する。 |
| 正常消費された管理元ZIPから保留登録・推定への継続 | `BMSLibrary.PackageInstall` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryManagedArchiveInstallTests.cs) の `AutoInstall_ArchiveAndFolderInputsReachInstallOrPendingEstimation`（リソース不足）: 管理ZIP・借用ZIP・フォルダから同じ既知候補を推定し、保留行と実入力を保持する。正常導入との分担は[ドロップ導入](drop-install.md#実装とテストの対応)を参照する。 |
| 実背景入力更新の非重複と受理済み更新保持 | `CatalogChartInfoOwner`、`ChartFileOperationSynchronizer` | [`PackageInstallWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) の `ProductionHydration_BlocksNewImportUntilActualInputPublicationCompletes` は実索引公開中の新規取り込み拒否と終端後成功、`ProductionImport_StopsDispatchAndApplyThenJoinsSuccessfulSiblingBeforeAdmissionRelease` は推定中に受理した実hydrateが入力を変えず、推定終端後に実DBの新しい行を索引へ反映することを確認する。 |
| 開始済みLR2全体同期と受理済みmaintenanceの交差 | `Lr2SynchronizationOwner`、`BMSLibrary.InstallableMaintenance` | [`InstallableMaintenanceAdmissionTests`](../../../BeMusicSeeker.Tests/Startup/InstallableMaintenanceAdmissionTests.cs) は実初期化で受理したmaintenanceと実LR2 Queueを接続し、全体同期の共通受付実終端待機、LR2→maintenanceの反映順、両方のDB更新と全開始Task終端、次の明示受付を確認する。 |
| 標準構成の全試聴・保留目録・設定・入力再読込み入口 | `ApplicationComposition`、各機能の管理主体 | [`ApplicationCompositionTests`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs) の `StandardComposition_RejectsCatalogPlaybackAndSettingsBeforeSideEffectsWhileKeepingDraftAndSelection` は共通受付の保持中に実入口を呼び、保留目録の拒否、player開始0、設定保存0、再読込みのBusy、draft・一覧・選択と取消、解放後の明示成功を確認する。試聴同士の順次実行と停止の保証は[音声](../runtime/audio.md)に従う。 |
| 現在の保留との照合、先行成功、保存と必須反映の集約 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)、[`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) |
| 画面の終端、異常報告と後続の失敗 | [`PendingPackageWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、[`MainWindowPendingPackageMutationViewTerminal`](../../../BeMusicSeeker/Views/MainWindow/MainWindowFeatureTerminals.cs) | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs)、[`MainWindowPendingPackageMutationViewTerminalTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPendingPackageMutationViewTerminalTests.cs)、[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) |
| 保留項目の編集結果を実体化済み行と次要求へ反映、有効パスと両表示空・空編集後の候補・推定WARNING保持 | [`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageChartEntry.cs)、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、[`LibraryChartRow`](../../../BeMusicSeeker/ViewModels/ChartList/LibraryChartRow.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `PackageChartSourceRows_InstallDestinationEditsNotifyCurrentProjectionAndPreserveEstimation`: adapterless BMSONの入力行・表示行を先に読み、候補B→有効パスと両表示空→空の受理済み編集結果を通知時点の現値、保持候補・推定WARNING、次の検索要求で確認する。編集受付と受渡しは [`RegularChartNavigationTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNavigationTests.cs) の `InstlDstCellEdit_UsesPendingOwnerWithExactChartTargetAndText` で確認する。 |
| 導入先変更の共通完了通知、即時反映と明示的クリア | [`PendingPackageWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs) | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) の `SearchPendingAsync_LooseTargetPublishesTransientProjectionAfterGateRelease` は排他解放後の単一通知を確認する。[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `CorrectInstallDestinationSearchAndClearPreserveFullScanPresentation` は全件確認の実操作から候補・警告の更新とクリア、Rows・選択保持、次要求の現在値を確認する。 |
| 導入先編集での推定情報保持と明示クリア、パッケージ内適用と拒否時保持 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) の `SetPendingInstallDestination`、[`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageChartEntry.cs) の `ApplyInstallDestinationMetadata` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs): `SetPendingInstallDestination_FromLowConfidenceCandidates_PreservesWarningAndSuggestions` は2譜面への反映と別パッケージ保持、`SetPendingInstallDestination_WithManualDirectory_PreservesLowConfidenceState` は空・候補外入力と拒否状態保持、`SetPendingInstallDestination_MetadataMismatchEditsPreserveContextUntilExplicitClear` は1候補の選択→空→手動編集と明示クリアでの他カテゴリWARNING保持を確認する。 |
| 手動入力の絶対ドライブルート、実在・所持検査と通常directoryの表記 | `BmsLibraryInstallEstimationService.ValidateInstallDestination` | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) の `ValidateInstallDestination_PreservesOwnedDriveRootAndRejectsUnknownRoot` は専用driveのroot directory/直下fileを独立した絶対rootへ照合し、別のdrive current directoryと非所持root拒否を区別する。`ReturnsResolvedDirectoryForPendingPackage` は通常directoryの末尾separator有無を確認する。欠落先・adapter非依存BMSONの既存系列も維持する。UNCは変更箇所と既知directory索引の静的互換確認へ分担し、実共有接続の実行保証とは区別する。 |
| 同一導入sessionの先行物理成功を使うリソース専用判定 | `BmsLibraryPackageInstallService` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `InstallPendingPackagesToEstimatedDestinations_ReevaluatesResourceOnlyAfterEarlierPhysicalSuccess`: 先行成功の所有overlayに基づく再判定を維持し、推定入力の世代再評価とは区別する。 |
| 同一操作の入力再利用、並列度、検索中状態の解除と初期単体範囲 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[`PendingEstimateSourceBatchSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PendingEstimateSourceBatchSnapshot.cs) | [`PackageInstallWorkflowOwnerTests.ProductionImport_AllOwnedSplitKeepsInitialPackagesWithOrWithoutCancellation`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) は全既所持の通常分割入力で、正常時・登録後取消とも2単体／2DB行を保持し、準備・work itemなし、取消結果・登録事実・SEARCHING解除・受付終端・明示clear健康度を確認する。発見の条件表は `BmsLibraryPackageInstallServiceTests`、入力再利用・通常推定・編集・表示は `BmsLibraryPendingPackageRegroupTests` が分担する。 |
| 入力相対配置からの候補根、根ごとのユニークMD5票と全体一致件数 | `BmsLibraryInstallEstimationService.TryResolveInstalledDestinationFromPackage` | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) の `ResolvesOnlyMatchingRelativePackageRoot`、`RootAndChildChartsVoteForSameRoot` は単体・直下・子・多階層・根外・末尾不一致とexact directory補助値を確認する。`DuplicatePrimaryHashesKeepTie`、`UsesEveryRelativePositionOfSameHash`、`UsesOnlyNonblankMd5` はコピー数・名前・順序、同hashの全相対位置と複数所持先、根別票と全体一致件数、両形式のMD5欠損・短い非空値を確認する。既存の両形式最多・同票とStrong・音源条件は維持する。 |
| 実混在取り込みの子hashから候補根への推定と相対位置を一度だけ付ける実導入 | `PackageInstallWorkflowOwner`、`BMSLibrary.PackageInstall`、`BmsLibraryInstallEstimationService` | [`PackageInstallWorkflowOwnerTests.ProductionImport_ChildOwnedHashResolvesOnlyMatchingPackageRoot`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerEstimationTests.cs) は実Enqueueからidleまでで、親のSTAGEFILEと子音源を持つ3譜面を一packageとして発見し、根直下に所持譜面のない候補へ未所持2譜面のDSTを設定する。既所持項目の空DSTを保ち、評価対象への適用と区別する。同じpackageとDSTを明示導入入口へ渡し、実FS・SQLiteの親譜面・画像と子譜面の配置、既所持譜面のbytes・所持MD5・DB hash保持と保留解除を確認する。自動継続ONは既存の `ProductionImport_CompleteSourceAudioStillEvaluatesKnownHashCandidates` へ分担する。末尾構成不一致では別資源候補があってもDST空・保留・入力譜面と資源の内容保持、解決失敗警告を確認する。 |
| 導入済み対象だけのリソース上書きに必要な実配置の一致 | `BmsLibraryInstallEstimationService.TryPrepareInstalledOnlyPackageDestination`、[`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`InstalledOnlyResourceOverwriteValidationTests`](../../../BeMusicSeeker.Tests/Install/InstalledOnlyResourceOverwriteValidationTests.cs) は既存の全8ケースに加え、`RequiresCommonConvertedRoot` と `MultipleLocationsRemainAmbiguousBeforeRootConversion` で直下＋子の共通根・不一致・別根と、変換前の複数所持先拒否を確認する。[`BmsLibraryPackageInstallServiceTests.OverwritePendingInstalledOnlyPackagesResources_PreservesChartsAndUpdatesChildResourceAtPackageRoot`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) は実FS・SQLiteで子音源bytesだけを更新し、譜面bytes・MD5・件数、相対配置と保留解除を確認する。 |
| 評価入力の変更不能性とリソース相対キー | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs) | [`ChartResourceSnapshotTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceSnapshotTests.cs) |
| 空キー・種類不明から親参照理由を生成せず、正常なキー・件数・ハッシュと解析採用範囲を保持 | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs)、`BmsChartFileParser`、`BmsonChartFileParser` | [`ChartResourceSnapshotTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceSnapshotTests.cs) の `Create_ExtensionOnlyBmsResourcesKeepNormalKeysWithoutParentTraversal` は小さい実BMSから投影配列の有無、単体・集約を確認する。`AnalyzeReferencePathForLookup_PreservesExistingPathClassification` と `Create_UnknownResourcePreservesOnlyActualParentTraversal` は解析・種類不明の境界を確認する。[`BmsonSongParserTests`](../../../BeMusicSeeker.Tests/ChartInfo/BmsonSongParserTests.cs) の `Parse_ExtractsResourceReferences` は実bmsonから直接・投影・集約を確認する。 |
| 空キー等があっても既存候補を推定し、実親参照では警告して保留 | `BMSLibrary.PackageInstall`、[`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs) | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) の `SearchEstimatedInstallationDirectory_CurrentDirectoryResourcePath_NormalizesAndEstimates` は本番解析から候補・保留理由・警告なしを、`SearchEstimatedInstallationDirectory_UnsupportedParentResourcePath_WarnsAndSkipsEstimation` は実親参照の警告・保留を確認する。 |
| CP932デコード非対応の理由を単体・集約に保持し、親参照と区別 | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs)、`Lr2CompatibilityEvaluator` | [`Lr2CompatibilityGoldenTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2CompatibilityGoldenTests.cs) の `EvaluatorFlagsCp932DecodeUnsupportedResourceAndContinuesLengthEvaluation` は入力バイト列から理由の保持と親参照falseを確認し、既存の符号化・長さの評価も維持する。 |

| 再導入の唯一Strong・複数Strong・現配置改善・同梱不使用 | `BmsLibraryInstallEstimationService` | `BmsLibraryInstallEstimationServiceTests` の再導入系列がRelated共存の唯一Strong採用、異順位でも複数Strong拒否、改善なし・音源未確認の拒否、同梱で救済しないことを確認する。 |
| 一操作の保持モード、生成済み行へのRelated表示、改名・移動後の候補編集と絶対drive root入力 | `PendingPackageWorkflowOwner`、`MainWindow`、共通分類器 | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) が要求方針と対象・元の編集Taskと受付を、[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `PendingEstimationAndCandidateEditingReachRealizedRowsWithRelatedEvidence` がcompiled treeの実推定、改名・別root移動後の子候補と絶対drive rootのcompiled入力から項目・現在行・再編集初期値、各回の実受信Task・Presentation・L終端、root直下譜面の非対象保全、空編集/クリアと別パッケージ保持を、[`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) が受信側投影を分担する。 |
| Related根拠の確定値引継ぎ・失効後の再評価 | `BMSLibrary`、`PackageChartEntry`、`CatalogOwnedCollectionOwner` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) と [`CatalogMaintenanceCurrentValueTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMaintenanceCurrentValueTests.cs) が群と候補編集、プロフィール依存の失効を確認する。 |

## 関連資料

[メタデータ照合の設計判断](../../decisions/install-estimation-metadata-matching.md)、[データと索引](../core/data-and-indexes.md)、[警告](warnings.md)、[変更操作](mutations.md)、[共通並行処理](../core/workflow-concurrency.md)を参照します。
