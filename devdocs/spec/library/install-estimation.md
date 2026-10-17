# 導入先の推定

## 目的と適用範囲

保留パッケージや選択譜面について、所持ハッシュまたは使用リソースから既存フォルダを選び、Strong成立と導入後の音源完全性を自動設定の必要条件とする仕様です。入力、候補抽出、順位、確信度、画面への適用を定めます。実際の導入の受付・保存・失敗は[変更操作](mutations.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

本資料の「候補」は既存の譜面フォルダです。「充足率」は要求リソースのうち導入後に利用できる割合、「自身の所有分」は子孫から集約された分を除いたフォルダ自身のリソースを意味します。

## 仕様

操作の受付分類・競合結果・必要継続の寿命は[競合ポリシー](../core/operation-concurrency-policy.md)を正本とします。以下は入力・処理・資源所有と結果の固有契約です。

### 入力とリソースキー

対象はパッケージまたは単独の譜面です。[取得済みの共通リソース結果](chart-model.md#譜面情報とリソース保守)の使用参照を `ChartResourceSnapshot` へ索引化し、複数譜面では検索キーを和集合にして音声・画像・動画・任意画像を分けます。分母は使用IDから解決した種別別のユニークな要求ファイルキー数です。未使用定義を含めず、同じファイルへの複数ID参照は一件とします。抽出条件は[軽量解析の使用リソース](parser-compatibility.md#軽量解析の使用リソース)に従います。原文・用途・解析状態は失わず保持し、未取得を保存主体やファイルから隠れて補完しません。

BMS/PMSの未定義WAV/BMP IDとBMSONの参照先ヘッダーのないBGA/LAYER/POOR IDは、必要集合へ加えず、健康度・不足警告・導入自動設定へ影響させません。定義済みの使用参照の不正パスや空name・取得失敗は正常な要求0件へ置き換えません。未使用定義だけの親参照は使用要求の非対応理由にしません。

キーは拡張子を除いた譜面相対のパスです。`foo.wav` と `./foo.wav` は `foo`、`sound/./foo.wav` は `sound/foo`、`sound/foo.v2.wav` は `sound/foo.v2` になります。`foo` と `sound/foo` は別であり、ファイル名だけに戻して一致させません。既に正規化した参照をパッケージ集約時に再正規化しません。

パス解析で独立した `..` セグメントが見つかった場合だけ、親参照の非対応理由を登録し、索引のキーへ入れません。`sound/../foo.wav` も対象ですが、名前中の `foo..bar.wav` は親参照ではありません。実際の親参照が対象に一件でも含まれる場合は推定せず、`UnsupportedResourcePath` を表示します。

`.wav`、`.ogg`、`.png` など拡張子除去後のキーが空になる参照は索引へ追加せず、親参照の理由にも使いません。種類不明だけでもその理由を生成せず、未知拡張子の既存の解析採用範囲、空文字・不正パス・ドライブ絶対パス・先頭区切りの既存の正規化を維持します。既に解析された非対応理由は単体・パッケージ集約へ引き継ぎ、CP932のデコード非対応を親参照へ置き換えません。

### 入力元に同梱されたリソース

通常の導入推定では、候補のリソース `C` とパッケージが持ち込む `BundledResources` の集合 `S` の和集合を評価します。既所持だけのパッケージも同じ入口を使います。再導入先の修正では、候補のリソースだけを評価します。

ディレクトリ単位のパッケージは、そのルートだけを再帰的に調べます。一ルートあたりの訪問上限は50000件です。上限を越えた時点で止め、部分結果を推定に使わず `SourceSurfaceScanLimitExceeded` を付けます。複数パッケージの合計件数ではありません。全件を先に取得する方式ではなく、訪問中に上限を確認します。

列挙の失敗分類、継続条件と不完全性は[上限付き入力元列挙](../core/path-length-and-io.md#推定用探索の上限)に従います。

上限超過以外の通常失敗では、取得済みの部分資源を推定入力として使う既存の方針を維持します。`PackageInstallEstimationSnapshotBuilder` は列挙の成否・完全性と観測済みの譜面・資源・追跡対象件数、不完全理由を診断します。診断先の失敗で推定条件を変えず、新しい拒否理由へ変換しません。この部分結果をライブラリの削除判定や索引置換の根拠には使いません。

譜面の発見結果は、列挙の成功・完全性と全譜面の読取り成功が成立した場合だけ完全な一覧とします。取得できた所属列は保持しますが、部分列を全所持・全選択の証明へ昇格しません。フォルダ削除の前には対象だけを改めて確認し、[削除対象と部分失敗](mutations.md#削除対象と部分失敗)に従います。通常表示・推定のためのパッケージcacheへ削除許可の状態を持たせません。

単一BMS・BMSONと単独譜面では、親をパッケージ範囲として列挙しません。`SourceDirectory` は入力元の候補除外や診断に使いますが、同梱リソースと入力元候補の集合は別の空集合です。走査時間、件数、ハッシュ生成時間、訪問件数は0、走査方式は空、上限超過はfalseとします。相対参照があることを親走査の理由にしません。

### ライブラリ側の索引

リソース経路の候補は `DirectoryResourceLookupCache.Keys` に含まれる譜面フォルダです。リソースだけの下位フォルダは候補にせず、入力元自身も除きます。

照合の正本は音声・画像・動画別の相対キーと逆引きです。ファイル名だけの集合や全種類を混ぜた集合を、候補抽出・最終照合・音声条件の代わりに使いません。リソース経路は索引がない場合に推定不能とし、全ライブラリの走査に切り替えません。所持ハッシュによる候補選択自体は資源索引なしでも保持しますが、自動適用には使用音源の完全性を評価できることを要求します。起動時走査を無効にした設定では、推定できない場合があります。

### 入口ごとの候補

| 入口 | 候補と選び方 |
| --- | --- |
| 通常推定 | 未所持譜面を含むパッケージと既所持だけのパッケージについて、外部の譜面フォルダを探す |
| 既所持・未所持が混在 | 既所持ハッシュの実配置先を数え、単独最多ならそのフォルダを選択し、未所持群とのStrongを検証する。同点ならその候補集合だけをリソース・基本情報で評価する |
| 全対象が既所持 | 全対象のハッシュ一致数が単独最多ならそのフォルダを選択し、全対象とのStrongと同梱込みの音源完全性を検証する。同点ならその候補集合を評価する |
| 単独bmson | 所持先が一意ならそのフォルダを選択し、その譜面とのStrongを検証する |
| 再導入先修正 | 現在の配置先を除いた既存フォルダを評価する。現在配置の状態は改善判定の基準だけに使う |

混在パッケージでは、配置先候補が解決できない場合、または候補限定の評価に必要な索引がない場合、通常推定へ切り替えず `InstalledDestinationResolveFailed` とします。候補の基本情報まで同等なら、フォルダ内の一意な主ハッシュ数を補助条件とします。選択候補が後述の強一致を満たす場合だけ、単独最多を自動設定できます。ハッシュ候補選択の成功と、自動設定の許可は区別します。単独最多・一意候補がStrong不成立または比較不能なら、同じ候補と代表表示を保持して導入先を空、`Low / MetadataMismatch`、自動適用falseとします。ハッシュ解決失敗へ変えず、少数側や別のリソース候補へ再探索しません。

選択候補が `Strong` と音源完全性を満たしても複数候補が解消しない場合、`AutoApplyAmbiguousInstallDestination` が有効なら自動設定と候補表示を行い、`InstalledDestinationAutoAppliedAmbiguous` を残します。設定無効なら導入先を空にし、`InstalledDestinationAmbiguous` と候補を表示します。選択候補が `Strong` を満たさない場合は、設定によらず `Low / MetadataMismatch` として自動適用せず、候補を保持します。

### 候補の絞込み

まずカテゴリ別の逆引きで参照キーに合う候補を集めます。候補が0件でも全ライブラリへ戻らず、`no_viable_destination_below_threshold` とします。

音声参照が二件以上なら候補自身で二件以上、一件なら一件以上の一致を要求します。音声参照がなければこの条件はありません。さらに、実際に使える音声の充足率が `innerWavHealthThreshold` を超える見込みがない候補を除きます。通常導入は同梱分を含め、再導入は候補だけで判断します。要求キーが全カテゴリで0件の場合、リソース一致から新しい候補を作りません。所持ハッシュ等の別の根拠による候補は維持します。

#### 通常推定の候補集合

リソース一致を使う通常推定で、候補をどこまで絞るかを示します。矢印は候補集合の絞込み・評価への入力であり、候補がなくなった後に全ライブラリへ戻る経路はありません。混在・全既所持・再導入の候補集合は前の表で別に定めます。

```mermaid
flowchart TB
    Keys["カテゴリ別の譜面相対キー"] --> Reverse["カテゴリ別逆引きに一致する譜面フォルダ"]
    Reverse --> Exclude["入力元自身を除外"]
    Exclude --> AudioCount["音声参照がある場合だけ：候補自身の音声一致数で絞る"]
    AudioCount --> AudioHealth["音声参照がある場合だけ：同梱分を含む音声充足率の見込みで絞る"]
    AudioHealth --> Candidates{"候補が残るか"}
    Candidates -->|いいえ| None["有効候補なし：全件走査で救済しない"]
    Candidates -->|はい| Evaluate["同梱分を含むリソース評価・順位・祖先抑制"]
    Evaluate --> Result["基本情報と確信度を判定して結果を返す"]
```

最終的な有効候補・自動適用の判定は以下の各節に従います。索引利用不能と、評価した結果の候補なしも区別します。

### 評価値と順位

要求集合を `R` とし、カテゴリごとに導入後一致数 `|R∩(S∪C)|`、候補自身の一致数 `|R∩C|`、候補自身の件数 `|C|` を求めます。充足率は導入後一致数を要求数で割った値、適合率は候補自身の一致数を候補件数で割った値、Jaccardは候補自身の一致数を `|R∪C|` で割った値です。再導入では `S` を空とします。

有効候補の主な充足率は、音声要求があれば音声、なければ要求のある画像・動画・任意画像の最大値です。この値が70%を厳密に超えることを要求します。表示整数から判定せず、一致数×100と要求数×70を比較します。70%は候補提示のしきい値であり、自動確定の音源完全性を緩めません。

順位は音声、画像、動画、任意画像の順で比較し、各カテゴリ内は同梱込み充足率、候補自身の一致数、候補自身のJaccard、適合率の順です。要求0件のカテゴリは比較しません。最後にディレクトリのパスで順序を安定させます。比率は表示用に丸めず、元の比で比較します。同梱98件と候補2件、同梱98件と候補90件がともに全要求を満たすなら、候補自身の一致が大きい後者を上位とします。同順位・メタデータ比較範囲・祖先抑制も同じ指標を使います。

### 比較用の基本情報と導入先の代表ペア

推定対象はその要求で選んだ譜面集合、導入先候補は既存の実パス索引から取得した所持譜面集合を集計します。両側とも共通現在値の `RawTitle` と `Artist` を使い、BMSの別フィールドの副題やbmsonの副題・譜面名をTITLEへ合成しません。原題が空なら表示用 `Title` で補いません。bmsonのtitle省略・null・空文字も原題なしとして保持し、表示用の合成と入力の受理範囲は維持します。

TITLEとARTISTはNFKC・空白整理を行い、小文字化前の表記と、その `ToLowerInvariant()` による比較キーを対応させます。ARTISTは従来どおり `obj`・`note`・`notes` の差分マーカーを大文字・小文字を区別せず除去し、スラッシュ後の差分表記も除去します。元の譜面値・保存値は変更しません。

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

`INSTL DST TITLE` / `INSTL DST ARTIST` は採用グループ内でパスの `OrdinalIgnoreCase` 順、続いて入力順の先頭レコードを選び、同じレコードの小文字化前の照合表記を一緒に表示します。表示を小文字化した二要素は比較用の採用ペアに一致します。有効ペアがなければ両列が空欄、ARTISTだけが空ならTITLEだけを表示します。表示が空であることを導入先や候補の有無の判定に使いません。

自動設定先、未設定時の有効な先頭候補、手動入力・候補選択、所持先の直接解決、再グループ化の全経路で同じプロファイルを使います。既存キャッシュの所有・寿命・失効を共用し、所持現在値の更新後は比較キーと両表示表記を同じ再集計で更新します。

対象の実在最頻ペアは混在パッケージでは未所持群、全既所持パッケージでは全対象、単独・looseの既存個別評価では各譜面から作ります。所持群を混ぜて未所持群の不一致を埋めず、全譜面それぞれの一致も要求しません。

### メタデータの強一致と同順位の解決

対象Tと候補Cの唯一の強一致条件は、両TITLEが非空、正規化ARTISTが同値、正規化TITLEのDice係数が0.88以上であることです。ARTISTの空文字同士は一致し、片側だけ空なら不一致です。同一非空TITLEの類似度は1.0、それ以外はUTF-16単位の重複回数付き2-gram Dice係数で比較します。TITLE単独完全一致・ARTIST単独一致・ペア完全一致の別経路や、0.72以上の弱一致による確定は使いません。

リソース評価値が先頭候補と等しい有効候補だけを、対象との強一致の真偽で比較します。通常は最大3候補です。混在パッケージの候補限定評価で一意ハッシュ数の取得処理が渡された場合だけ、同等候補全体を対象にします。画面の候補表示はいずれも上位3件までです。リソース主順位が異なる候補の順序はメタデータで逆転させません。

Strong真を偽より優先し、真真または偽偽はメタデータ上同順位とします。TITLE完全一致・Diceの大小・支持数でさらに一意化しません。同順位なら許可された一意な主ハッシュ数を補助条件とし、最後は元のリソース順位とパス順に戻します。選択候補だけがStrong真なら `metadata_tiebreak_distinct`、双方Strong真でハッシュ数が一意なら `directory_hash_count_tiebreak_distinct` として高確信度にできます。双方Strong偽をハッシュ数や順位差で高確信度にしません。

### 祖先候補の抑制

子孫のリソースが祖先の譜面フォルダからも見える場合、同等以上の評価を持つ子孫があり、祖先自身の一致数が0、子孫自身の一致数が1以上なら祖先を抑制します。親子関係のある候補だけを調べ、自身の所有分の照合も必要なときだけ行います。

### 確信度と画面への適用

候補の有効性はStrongより前の経路固有要件を表します。リソース経路は候補の充足条件、ハッシュ経路は単独最多・一意条件です。自動設定の許可は共通の `ShouldAutoApplyDestination` で表し、すべての経路で定義済み音声参照に照合不能がなく、同梱と候補を合わせて使用音源の欠損が0件であることを要求します。音声要求0件もこの条件を満たしますが、Strong・関連性・曖昧性の条件は残ります。BGA・動画・補助画像の欠損はこの必須条件に加えません。

所持ハッシュで直接選んだ候補も同じ完全性評価を通します。候補抽出用の音声最低一致1～2件はハッシュ根拠には課しません。必要な候補リソースを評価できない場合は100%と仮定せず、候補と代表表示を保持して自動適用しません。

有効候補がある場合、高確信度と自動適用には、対象と選択候補の強一致を必須とします。Strong不成立、非空TITLEの実在ペア欠如、リゾルバー未指定・空プロファイルは `Low / MetadataMismatch`・自動適用不可とし、有効候補とWARNINGを保持します。TITLE単独一致、リソース順位差、一意ハッシュ数、曖昧候補自動適用設定では救済しません。候補側に有効ペアがあれば、その表記を表示します。

複数のStrong候補が補助条件でも解消しなければ、既存の曖昧候補として扱います。曖昧候補自動適用設定が有効なら、Strongと音源完全性を前提に低確信度・候補・曖昧WARNINGを保ったまま自動設定できます。再導入では複数の有効候補を自動適用せず、一意でも現在配置より改善することを必要とします。所持ハッシュからの単独最多・一意解決にも同じStrongと音源完全性を要求します。有効候補がない場合の内部確信度は従来どおりです。手動入力・候補選択、保存済み値の復元、再グループ化の確定済み値引継ぎは推定による自動設定ではないため、低層setterで再検査しません。

| 結果 | 適用 |
| --- | --- |
| 有効候補なし | `Confidence=High`、導入先null、自動適用false。候補がないという判定の確信度であり、導入成功を意味しない |
| 一意な有効候補でStrong成立・使用音源が完全 | 高い確信度で自動設定する |
| Strong成立し、リソース同順位をStrong真偽または許可された一意ハッシュ数で解消 | 高い確信度にできる |
| 解消しない複数候補、基本情報の不一致・比較材料不足 | 低い確信度と候補を返す |
| 音源の欠損・定義済み参照の照合不能・完全性の評価不能 | 候補を保持し、自動設定しない。曖昧候補自動適用設定でも解除しない |
| 再導入で現在配置より改善しない | 低い確信度と候補を返す |

`INSTL DST` は、導入先があり `ShouldAutoApplyDestination=true` の場合だけ自動設定します。題名・アーティスト列、候補、警告も同じ結果から更新します。保留のリソース状態は代表譜面だけでなく全 `PackageChartEntry` に投影し、既所持・単一ファイル・入れ子の警告と両立させます。

保留の推定・手動変更・クリアと、導入済み譜面の再導入先推定・クリアは、操作結果から一覧へ反映する共通経路を使います。変更した譜面と一覧への影響を一つの操作完了通知で渡し、画面切替なしで導入先・代表情報・候補・推定警告を更新します。空の編集値は導入先と代表TITLE/ARTISTだけを消去します。候補選択と候補外の有効な手動入力は、既存のパス検証とパッケージ内の適用範囲に従って導入先・代表情報を更新します。いずれの編集でも候補と推定WARNINGは保持し、拒否された編集は状態を変更しません。「インストール先をクリア」は導入先・代表情報・候補・推定WARNINGを消去し、他カテゴリのWARNINGを保持します。空編集と明示クリアの両方で、次に表示行から作る要求へ古い導入先を残しません。再推定と実導入で状態を置換する既存の契約は維持します。

保留パッケージでは項目の状態を正本とし、後続の項目変更を古い一時状態で覆いません。通知・投影の経路が異なる通常のライブラリ行と対象集合行も同じ表示契約を満たします。表示行のキャッシュと並べ替え・絞込みの更新は[一覧表示](../ui/table-view.md#絞込みと画面更新)に従います。

### 自動推定と並列処理

新規取り込みで保留に入ったパッケージは、使用音源に欠損または定義済み参照の照合不能があれば一括処理の単位で自動推定します。入力元の音声充足率が70%を超えていても省略しません。使用音源が完全で既所持ハッシュの候補がない場合は、リソースから新たに候補を探す自動処理を開始しません。非音声だけの不足を理由に厳格設定で保留になった場合も、この新規探索を省略します。既所持ハッシュで一意・単独最多または同点の候補が分かる場合は、完全音源でも既存の候補集合とStrong評価を残します。混在時は未所持群を評価し、Strongと音源完全性が成立すれば導入先を設定します。不成立・比較不能では候補・代表情報・警告を保持して未設定とし、候補外へ再探索しません。手動推定は省略しません。自動新規導入の分類は[設定仕様](../runtime/settings.md#自動新規導入に要求するリソース)に従います。起動時に復元した保留は自動推定せず、利用者の明示的な手動推定で処理します。

一件の手動推定では候補評価を並列化し、`asParallel=true` の並列数は `max(1, CPU数 - 1)`、falseなら1です。複数パッケージの一括推定は外側を並列化し、各候補評価は1にして二重並列を避けます。自動推定の外側並列数は `PendingInstallEstimateMaxParallelPackages`、0ならCPU数から決めます。手動の複数パッケージも同じ一括経路を使います。

全選択譜面が保留パッケージに属する場合はパッケージ単位へまとめられます。単独譜面が混じる場合と再導入先修正は外側を逐次実行し、内側の候補を並列評価します。取り込みで取得した参照集合は同じ対象の評価へ再利用します。

取り込みから推定・regroupへの直接継続と受付終端は[競合ポリシー](../core/operation-concurrency-policy.md#ライブラリ保留保守)に従います。物理変更leaseの解放を推定完了と扱わず、確定済み登録・適用済み結果・未適用の保留を実結果へ区別します。

### 推定入力の確定と停止

受理後に現在対象・所持未所持の分割・評価入力を一度捕捉します。取り込みが捕捉した設定は直接推定の並列度と評価にも使います。準備した入力元のリソース参照集合と分割は同じ操作内で再利用します。譜面情報の読込み・ハッシュと譜面情報の補完・所持保守・LR2同期など、推定入力を実際に更新する受理済み背景処理も同じ論理受付を使います。背景処理は先行操作の実終端を非同期で待ち、Busyで捨てません。通信・表示キャッシュ・リソース健全性の読取り表示は、それだけを理由に全体待機へ含めません。独立キャッシュと所持集合の版、通知順、再接続の識別、物理変更権限、リソース健全性の入力保護は各責務へ残します。

取消または最初の評価例外を観測した後は、新しい評価のdispatchと結果のapplyを止めます。開始済み兄弟が後で正常終了しても未反映結果を適用しません。既に適用した推定と確定済み登録・導入は保持し、未適用対象を次の明示推定用に保留へ残します。開始済み全評価Taskと後片付けの終端までBusyを保ち、全対象のSEARCHINGを解除してから受付を解放します。結果には確定済み事実と実際の準備・評価の失敗・取消を保持し、失敗を空の成功結果へ置き換えません。

### 表示用の再グループ化

発見時に `RegroupEligibleSourceDirectories` へ記録した入力元だけを、一括推定の末尾で再グループ化できます。同じ入力元の保留が二件以上あり、既にディレクトリパッケージがなく、遅延推定の対象が混在しないことを条件にします。

既所持譜面はハッシュから実配置先、未所持譜面は設定済みの推定先を使い、全対象が一つの導入先へ揃う場合だけまとめます。分割・解決不能なら変更しません。再グループ化後の導入先と題名・アーティストは未所持の項目だけへ設定し、全て既所持なら導入先は空です。警告とリソース状態は新しいパッケージ単位で初期化します。

### 推定先への実導入

短いモデルの保護範囲で現在の保留と照合し、入力順に重複を除き、有効な項目の導入先が全て空の候補を除きます。保護範囲を出てから適格な候補だけを捕捉し、除外した入力・行・導入先・警告は変更しません。

分類と物理処理は入力順です。所持判定には開始時の主ハッシュと `SessionSuccessOwnershipOverlay` の先行成功を使います。同一パッケージ内の同じハッシュを重複処理せず、既所持を除いた新規対象の導入先が空または複数なら保留を維持します。

実導入は一つの変更セッションへ集約します。全譜面が既所持でも、リソースだけ・後片付けだけの必要な変更は残します。追加BMS・BMSONと、リソースの移動先にある既所持譜面を末尾の保守対象にします。何も移動しない後片付けだけの成功では保守しません。

物理完了時のパス・導入状態の反映と、保守から所属登録への順序を維持します。必須後処理が全て成功した後、今回導入した譜面の同じ所持tokenから現在値を取り直し、加入済みentryへ計算済みの充足値を局所反映します。既存の延期通知をリース解放後に公開し、失敗・取消時に成功した後処理を装いません。

既存のリソース状態索引が有効なら対象だけを差分更新し、索引が未構築・無効なら、この後処理だけを理由に全体再構築しません。入力元の保全、確認済み残存物の削除、終端の報告は[ファイルとDBの整合](file-db-consistency.md)に従います。

### 診断

`estimate_install start` の評価方式、候補数、絞込み後件数、照合時間、祖先抑制、確信度と理由を確認します。同順位がある場合は `metadata_frontier` と `metadata_tiebreak`、一括推定は `packageDegree` を併せて確認します。

実導入の対象別記録は物理処理の単位であり、DB確定回数ではありません。`reverse_lookup_incremental_update`、`maintenance_update`、`resource_health_index_delta`、`chart_info_inline_install` で操作末尾の集約を確認します。

### 保留パッケージを新規として導入する確認

「新規としてインストール」は設定・推定された導入先を使用せず、新規インストール先へ導入します。推定、候補生成、リソース警告、導入処理自体は変更しません。

パッケージ内に導入先設定済みの譜面があれば、設定先を使わない旨を必ず確認します。全譜面の導入先が未設定の場合は、`ShowNewPackageInstallConfirmMsg`（既定 true）に従い、保留を意図的に新規扱いし、リソース不足等の WARNING が残っていても導入する旨を確認します。この設定を false にしても設定済み導入先の確認は省略しません。

確認は共通論理受付の取得後、モデルの物理変更リース取得前に解決し、既定の応答を No にします。ボタンを選ばず閉じた場合も承認せず、拒否されたパッケージは導入先の有無によらず変更せず、入力元・DB・保留を保持します。複数選択では承認されたパッケージだけを既存の一括導入経路へ渡します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 譜面発見の完全性と破壊的操作の確認 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | 列挙の成否・完全性と全譜面の読取り結果から完全性を作る範囲は静的に確認する。実際の部分取得・部分管理列から削除への接続は[削除の対応表](mutations.md#実装とテストの対応)の実FS・DB試験、列挙失敗そのものは既存の `RootFileEnumerationTests` に分担する。 |
| 新規としての導入確認、設定による省略、拒否対象の保全 | `PendingPackageWorkflowOwner`、`BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService` | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) の `ForceInstallPackagesAsync_ConfirmsAccordingToDestinationAndNewInstallSetting` は要求の既定値を実表示部品で正規化した `ClosedByUser` 応答が未承認になることと項目保持を確認し、既存の確認条件・混在選択も維持する。[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `ForceInstallPendingPackages_PublicEntryUsesConfirmationSettingAndPreservesRejectedInput`: 公開入口で未設定ONの新規確認、未設定OFFの無確認導入、設定済みOFFの確認維持、No・未選択閉鎖時の入力元・DB・保留保持、肯定時の実新規導入を確認する。閉鎖応答は要求のボタンと既定値を `ThemedMessageBox.NormalizeDefaultResult`・`UiDialogResult.ClosedByUser` の既存変換へ通して生成する。明示的な未承認パッケージ保全の既存ケースも維持する。 |
| 新規としてのメニュー表示と既存操作経路 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `CompiledTreeMenusPreservePackageSectionsAndPendingOperations`、`CompiledSelectedChartRoutesPreservePendingTargetAndHandledBeforeCompletion`: ツリー・選択譜面の実ForceInstall項目Headerと表示リソースの対応、既存経路・対象・完了を確認する。 |
| 新規としての実導入先と設定先の非使用 | `BMSLibrary.PackageInstall`、`BmsLibraryPackageInstallService` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta`: 実導入先が設定済みExplicitと異なること、Explicitへ入力譜面・資源を作らないことを、既存の仕事量・通知・永続結果とともに確認する。 |
| 実在最頻ペア・空ARTIST・同票・区切り群とMIX終端・同一レコード表示 | [`InstallEstimationMetadataNormalizer`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/InstallEstimationMetadataProfile.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) の `BuildProfile_SelectsRealDominantPairAndDisplay`、`BuildProfile_ExcludesEmptyTitleAndAcceptsEmptyArtist`、`BuildProfile_TiesUsePathThenInputOrderForGroupAndDisplay`、`NormalizeTitleForTieBreak_UsesRightmostGroupAndMixEnd`。候補への表示接続は `EstimateInstallationDirectory_PopulatesRepresentativeMetadataForTopCandidates`。 |
| 唯一のStrong判定・Dice境界・同順位・主順位保持・比較不能 | `BmsLibraryInstallEstimationService`、`InstallEstimationMetadataNormalizer` | `BmsLibraryInstallEstimationServiceTests` の `ComputeTitleSimilarity_UsesNormalizedKeysAndCountedUtf16Bigrams`、`EstimateInstallationDirectory_StrongRequiresPairArtistAndTitleThreshold`、`EstimateInstallationDirectory_ExactTitleWithDifferentArtistsIsMetadataMismatch`、`EstimateInstallationDirectory_TiedCandidatesUseOnlyStrongThenHashCount`、`EstimateInstallationDirectory_StrongDoesNotOverrideResourceRanking`、`EstimateInstallationDirectory_MissingRealPairKeepsLowConfidenceCandidates`。閾値0.88、空ARTIST同値、再切断禁止、Strong真真の曖昧・ハッシュ補助、偽偽の不一致、リゾルバーなしを確認する。既存の再導入・候補なしケースも維持する。 |
| 使用集合、同梱込み充足と候補自身の順位、厳密な70%境界、0件 | [`BmsLibraryInstallEstimationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) で同梱98件と候補2件／90件、端数のしきい値、要求0カテゴリ・種別・相対パス・祖先抑制を確認する。 |
| ハッシュ候補の選択とStrong・音源完全性による自動適用 | `BmsLibraryInstallEstimationService.EvaluateInstalledChartHashDestination`、`BMSLibrary`、`BMSLibrary.PackageInstall` | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) はStrong成立・不成立・比較不能、音源の完全・欠損・未解決・評価不能と候補保持を確認する。ハッシュによる候補選択と自動適用を区別する。 |
| ハッシュ経路の集約単位・全既所持の通常推定・拒否後停止・一括結果反映 | `BMSLibrary`、`BMSLibrary.PackageInstall` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) で未所持群または全既所持群の代表、単独bmson、最多候補拒否後の迂回防止、候補・警告・SEARCHING・進捗終端を確認する。旧マージ専用入口の必要保証も通常推定へ移管する。 |
| 自動新規導入の設定と音源不足時の自動推定 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs)、`BMSLibrary.PackageInstall` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) で設定ON/OFF・音源／非音声不足・未解決・補助画像・他の保留理由を確認する。同一操作の設定捕捉、70%超でも不足時に推定する接続を代表ケースで確認する。`PackageInstallWorkflowOwnerTests.ProductionImport_UsesCapturedCompletenessPolicyAndEstimatesMissingAudio` はハッシュ根拠なし完全音源でwork itemを開始しないことと手動推定を分担し、`ProductionImport_CompleteSourceAudioStillEvaluatesKnownHashCandidates` は実混在取り込みで一意先Strong成立・拒否と同点限定集合を維持する。 |
| 混在取り込みのハッシュ候補拒否と保留確定 | `BMSLibrary.PackageInstall`、`BMSLibrary` | `BmsLibraryPackageInstallServiceTests.AutoInstall_MixedFolderHashMetadataMismatchKeepsPendingInputAndCandidate` は実フォルダ入力、実DB保留行、未所持入力・候補・表示・警告保持と受付解放を確認する。 |
| Strong不成立の候補・WARNINGと生成済み行への投影 | `BMSLibrary`、`PackageChartEntry`、`LibraryChartRow` | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) の `SearchEstimatedInstallationDirectory_MetadataMismatchLeavesInstallDestinationEmptyAndShowsSingleSuggestion` と二候補の既存ケースが実索引・実BMS入力から不一致候補を確認する。[`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `PackageChartEntryDisplayRow_ReflectsUpdatedAdapterlessBmsonEntryState` は生成済み行へのMetadataMismatch・代表表示・候補・adapter非生成を確認する。判断の条件網羅はサービス側へ分担する。 |
| BMS・bmsonの合成表示前の原題入力、bmsonの原題省略 | `PackageInstallEstimationSnapshotBuilder`、`BMSLibrary`、[`BmsonChartFileParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsonChartFileParser.cs) | `BmsLibraryInstallEstimationServiceTests.PackageInstallEstimationSnapshotBuilder_BuildsTargetMetadataProfileFromDominantGroup` は実解析した両形式の対象入力、[`BmsonSongParserTests`](../../../BeMusicSeeker.Tests/ChartInfo/BmsonSongParserTests.cs) の `Parse_MissingTitleKeepsRawTitleEmptyAndComposedDisplay` は省略・null・空文字を確認する。候補側の原題と失効後の表示更新は `BmsLibraryPendingPackageRegroupTests` の`SearchEstimatedInstallationDirectory_MetadataTieBreakSelectsMatchingCandidate` の失効後の再推定、および既存手動設定・所持先解決・再グループ化のケースで確認する。 |
| 相対キー、カテゴリ、候補抽出、順位と確信度 | [`BmsLibraryInstallEstimationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs)、[`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) |
| 入力元の走査上限、単一ファイルとディレクトリの区別 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) の `PackageInstallEstimationSnapshotBuilder_DirectoryPackageDiscardsPartialSurfaceAtLimit` は上限超過で資源を破棄すること、単一ファイル・単独BMSONの既存ケースは親を走査しないことを確認する。[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) は実導入との接続を確認する。 |
| 取得済み部分資源の受入と消費側の診断 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | 上限超過以外で資源を破棄しない分岐と、成否・完全性・件数・理由の診断接続は静的に確認する。通常失敗時の推定入力への受渡しは自動テストでは未確認。列挙自体の失敗と独立ルート継続は[パスとI/Oの対応表](../core/path-length-and-io.md#実装とテストの対応)で分担する。 |
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
| 同一導入sessionの先行物理成功を使うリソース専用判定 | `BmsLibraryPackageInstallService` | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `InstallPendingPackagesToEstimatedDestinations_ReevaluatesResourceOnlyAfterEarlierPhysicalSuccess`: 先行成功の所有overlayに基づく再判定を維持し、推定入力の世代再評価とは区別する。 |
| 同一操作の入力再利用、並列度、検索中状態の解除、再グループ化 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[`PendingEstimateSourceBatchSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PendingEstimateSourceBatchSnapshot.cs) | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) |
| 導入済み対象だけのリソース上書きに必要な実配置の一致 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`InstalledOnlyResourceOverwriteValidationTests`](../../../BeMusicSeeker.Tests/Install/InstalledOnlyResourceOverwriteValidationTests.cs) |
| 評価入力の変更不能性とリソース相対キー | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs) | [`ChartResourceSnapshotTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceSnapshotTests.cs) |
| 空キー・種類不明から親参照理由を生成せず、正常なキー・件数・ハッシュと解析採用範囲を保持 | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs)、`BmsChartFileParser`、`BmsonChartFileParser` | [`ChartResourceSnapshotTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceSnapshotTests.cs) の `Create_ExtensionOnlyBmsResourcesKeepNormalKeysWithoutParentTraversal` は小さい実BMSから投影配列の有無、単体・集約を確認する。`AnalyzeReferencePathForLookup_PreservesExistingPathClassification` と `Create_UnknownResourcePreservesOnlyActualParentTraversal` は解析・種類不明の境界を確認する。[`BmsonSongParserTests`](../../../BeMusicSeeker.Tests/ChartInfo/BmsonSongParserTests.cs) の `Parse_ExtractsResourceReferences` は実bmsonから直接・投影・集約を確認する。 |
| 空キー等があっても既存候補を推定し、実親参照では警告して保留 | `BMSLibrary.PackageInstall`、[`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs) | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) の `SearchEstimatedInstallationDirectory_CurrentDirectoryResourcePath_NormalizesAndEstimates` は本番解析から候補・保留理由・警告なしを、`SearchEstimatedInstallationDirectory_UnsupportedParentResourcePath_WarnsAndSkipsEstimation` は実親参照の警告・保留を確認する。 |
| CP932デコード非対応の理由を単体・集約に保持し、親参照と区別 | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs)、`Lr2CompatibilityEvaluator` | [`Lr2CompatibilityGoldenTests`](../../../BeMusicSeeker.Tests/Lr2/Lr2CompatibilityGoldenTests.cs) の `EvaluatorFlagsCp932DecodeUnsupportedResourceAndContinuesLengthEvaluation` は入力バイト列から理由の保持と親参照falseを確認し、既存の符号化・長さの評価も維持する。 |

## 関連資料

[メタデータ照合の設計判断](../../decisions/install-estimation-metadata-matching.md)、[データと索引](../core/data-and-indexes.md)、[警告](warnings.md)、[変更操作](mutations.md)、[共通並行処理](../core/workflow-concurrency.md)を参照します。
