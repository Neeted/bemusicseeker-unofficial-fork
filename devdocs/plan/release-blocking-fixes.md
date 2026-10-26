# リリース前の必須修正

## 目的と採用範囲

通常の入力・操作で到達する保存、導入、一覧、再生、出力の不整合を解消する。本書は採用済みの修正対象と、既存の採用契約・未完了の実装・検証を扱う。対象は41件、実装の単位は下表の38単位とする。URL補完の取得元比較と候補比較、前曲の方向と非終端、登録範囲の改名保護と統合保護は、それぞれ一単位にまとめ、各不具合の判定は独立して残す。数値・日時統一は外部JSON LEVELの既存課題を包含する一つの横断単位とする。[任意改善](release-follow-up-candidates.md)は今回実装せず、リリース完了条件に含めない。

追加指摘は `dev` の `8c53fc9b` とLR2出力簡素化の作業差分を照合した静的調査を根拠とする。既存対象も含め、通常経路の確認を実再現・テスト成功と読み替えない。着手時に最新のコードと既存差分を確認し、解消済みの課題を再実装しない。LR2出力設定の保存・生成の契約は[設定の保存](../spec/runtime/settings-persistence.md)と[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)を参照する。

単体分割後の子作品発見、Everythingのドライブルート保持の2件は、利用者が必須修正に分類し、今回の実装対象へ追加した。元資料の基準 `6348b291` から `5d499a91` まで必要経路を静的に照合し、原因が残ることと既存計画との非重複を確認した。以下の2件の修正境界・代表判定は着手時に具体化する案であり、分類合意を詳細設計・テスト設計の承認と扱わない。

今回追加した登録範囲保護、保留回収・候補追従、導入採番、保存済み所属、BMT親MD5、数値・日時、前曲終端、フォーカス、ツリー編集、更新の課題は、`6e443a87` の必要経路を静的照合した。実再現・製品テストは未実施であり、原因が残る範囲を着手時に確認する。追加資料の識別子は既存計画の識別子と共通でないため、以下では機能名を主に扱う。フォルダ削除確認は[現行の三択契約](../spec/library/mutations.md#確認と共通受付)を維持し、未完了課題へ再登録しない。

以下の代表判定は独立テスト設計の代替ではなく、実装・テストが完了した保証でもない。

## 共通の変更境界

- 現行の入力受理、取消、受付、成功対象の一変更セッション、管理外ファイル・URLの保全を維持する。入力拒否の追加や意味の変わる代替は、合意した範囲を超えて導入しない。
- 設定は自身の候補と他項目の最新確定値を使う。受理済み入力の固定、候補排他、設定実操作の同時一件、必要なL/P受付、終了時の開始済みTask回収を維持する。通常閉鎖は候補だけを捨て、再表示は公開済み確定値から作る。
- 過去の失敗要求・成功段階を保持して後続操作を制限する方式を、最新入力の保存と保存済み正本からの再生成へ置換する。予約、自動再試行、自動巻戻し、永続修復台帳、専用修復管理、差分ごとの常駐完了辞書は追加しない。
- 少数変更の全件走査・全行実体化・全ソートを常用しない。既存の所有主体と通知・Taskを再利用し、旧処理を並存させない。同じファイルを扱うだけの理由で別機能を一括コミットしない。
- 新しい利用者向け文言は `Resources.resx`、`Resources.cs` と6言語辞書を同じ単位で揃える。現行仕様と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)の変更も該当単位に含める。

## ライブラリ操作で採用する契約

### 重複グループの選択と移動要求

手動選択と整理後の自動選択は、現在の検索結果内で一意な所属フォルダによる短命な識別へ揃える。二フォルダ統合・同一フォルダ整理は操作前に捕捉した次のグループ、三フォルダ以上の統合は存続グループを選ぶ。同名・空名でも取り違えず、対象消失は空結果とする。明示した重複ルートだけが全件を表示し、フォルダ単位選択との区別を保つ。見出しの最初の一致という旧経路、永続ID・選択永続化・追加の全件再検索は残さない。

初回重複解析中も許可される後発の画面移動を優先する。入力受付から表示採用まで同じ短命な要求識別を使い、古い画面切替と解析途中のsummary解除等の表示副作用を採用しない。必要なcache構築・catalog通知は完了させる。既存の一覧要求識別・取消を利用できる境界を確認し、画面移動をBusyで新規禁止する案は採らない。

## 着手順と実装単位

既存着手済みの単体分割後の子作品発見は成果を保って継続し、先行統合された設定テスト・仕様の成果を前提にする。次の新規着手は保存済み所属・編集値の喪失と前曲探索の非終端を最優先にする。数値・日時統一の横断契約を先に関係担当へ共有し、実装も早期に進める。任意のUI共通整理を必須修正の前提にしない。

表の番号は実装単位の識別であり、全件の直列順ではない。上記優先と次の依存境界で着手を決める。devへの統合は、依存関係・検証・独立レビューが揃った単位から一件ずつ進め、着手順やLR2関連作業を理由に独立した完成単位を保留しない。新しい実装・テスト・仕様・計画は各作業WTの別ブランチで完結させ、並列開発中のメイン作業ツリーはdevの統合専用として原則クリーンに保つ。独立した単位の調査・設計・編集は並行できる。全体ルートは利用者への質問、全体優先、単位間の依存と共有契約、WT割当、Functionalと性能測定の開始調整、dev統合と全体報告を担う。各単位責任者は範囲内の調査・テスト必要性と独立設計承認・引継ぎ点検・実装・Quick・仕様計画・レビュー・差戻し・branch内commitを完結させ、工程別の全体ルート再承認を求めない。小単位では責任者が実装を兼ねられる。未確定の利用者挙動・保証、範囲拡張、他単位や共有契約への影響は全体ルートへ戻す。各単位は必要な翻訳も含み、表中のクラス名は探索起点でありAPIや配置を固定しない。

並列化は次の依存境界で判断する。ファイルが別というだけで独立とせず、共有API、DBスキーマ、テスト補助、生成物、仕様の変更範囲を着手時に確認する。単に同じDB型を読むだけの理由で全件を直列化せず、共有境界を変更する必要が出た場合に再判断する。

| 作業群 | 直列に保つ順序・合流 | 並行できる範囲 |
| --- | --- | --- |
| 資源索引 | Everything資源directory別名化→ドライブルート保持。cache/scanの所有・一世代公開と生成DLLを同時に変更しない。 | 共有境界を変更しない解析・外部引数・表の局所修正等は別WTで進められる。 |
| 入力発見と導入推定 | 単体分割後の子発見→発見済み子パッケージの推定。子推定は子発見とEverything root保持の確定版へ合流してから接続する。同じInstallServiceを扱う保護ファイル採番は、子発見・子推定の確定版へ合流し、共有する物理計画helperの書込みを直列にする。 | 子発見は既存APIを消費し、Session/cache/共通helperを変更しない範囲なら別WTで進められる。登録readerの先行単位は取り込む。後から統合する側は新版で実導入・保留・cleanup・索引反映を確認する。 |
| 登録範囲と保留参照 | 登録範囲の改名・統合保護→保留候補・配置文脈の成功facts適用。同じFileOperations・MutationFactsとパス判定の変更は直列にし、確定DSTだけを変更する通常編集APIへ横断副作用を付けない。 | 登録範囲と保留候補は同じ資源索引を読むだけなら別調査が可能。共有書込みを含む実装は一writerで合流する。 |
| 重複一覧・選択 | 重複の所属識別→初回解析後の古い画面切替、右クリック現在セル→範囲選択主行は、それぞれ同じ通知・選択契約で順次扱う。 | 重複保守の共有境界を取り込んだ後、独立した表取得等と並行できる。 |
| URL・表の値と出力 | 取得の大小文字→補完元/候補の大小文字→転送アーカイブ保存名は同じ取得経路へ順次適用する。転送後相対参照、JSON出力、推薦prefix、名前なしsort、プロパティURL解除は、共通の表/URI正本やhelperの変更有無を確認して分ける。保存済み所属の追加・移動を先行し、親MD5の遅延解決とBMTコース往復は同じentry/exporterを直列調整する。同リリースのexporterVersion更新を一回へ集約する。 | 別の責務へ閉じるものは並行できる。同じ表の正本・保存APIまで広げる場合は同じ作業群へ戻す。 |
| 再生・成績・設定 | 前曲の方向・有限探索を先行できるよう、PlaybackPanelViewModelの共有gate・停止・世代契約と書込みだけを直列にする。保留の物理回収前停止/joinは前曲有限化の確定版へ接続する。一時停止の先頭戻し→一時試聴名→再開/再起動の相互依存は保つが、旧着手順だけで前曲を待たせない。設定の録音形式・履歴プリセット・追加先改名は同じSettings責務で順次扱う。 | IR派生値、外部引数は共通score表現・起動/終了基盤等を変更しない範囲で独立候補。真のAPI依存を着手時に確認し、同じownerの同時編集はしない。 |
| 数値・日時の横断境界 | 一責任者が形式と共有APIを揃える。BMS parser、BMSTableEntry/JSON、DB・設定の文字列境界、一覧formatter・日付部品、機械出力の契約を先に共有する。所属保存・親MD5・BMT版更新・設定の進行中成果へ合流し、共有helper・生成物・仕様の書込みを直列化する。 | 型付きDBを読むだけの単位は並行できる。意味のある内部分割とコミットは独立設計時に具体化し、UI共通整理を待たない。 |
| ツリー編集・再生フォーカス | EditableTextBlockの候補/終端とMainWindowの確定値取得を一単位にする。再生フォーカスもMainWindowを変更するため、その共有書込みと代表WPF fixtureを直列化する。 | 他の表・検索部品と同名の概念だけでは結合しない。任意の更新通知翻訳を採る場合もMainWindowの一writerへ合流する。 |
| 更新 | ドライブルート正規化→完成後ready公開→同期lock保持。Program/gateway、既存のmarker公開helper、テスト補助、配布生成物・更新仕様を直列確認する。 | 更新以外の共有境界を変更しない単位とは並行可能。実プロセス/drive検証は負荷と所有を別途調整する。管理file→directory遷移は任意の別原因。 |

作業WTの暫定最大5本・他チャット分を含む所有、原則一writer、子担当への委譲と責任者への返却、仕事の配分とイベント待機、安全な切れ目での移管と成功版の引継ぎは[並列作業](../spec/development/agent-workflow.md#並列作業)に従う。検証・レビュー待ちだけを増やさず、完了・統合・再利用や回収を進める。ignoredの資料・bin・検証結果が新WTへ自動到達したとは扱わない。

Functionalは全WT同時一件とし、別WTのQuickと必要ビルドは並行できる。Quickは単位責任者が開始・結果・回収まで自主管理し、Functionalと性能測定の開始調整は全体ルートが行う。同WTの編集・整形・ビルド・レビュー、共有出力と資源の衝突を避け、対象と必要依存を凍結する。実測負荷や資源問題に応じて調整し、開始済み処理を都合で中断しない。詳細は[検証仕様](../spec/development/testing.md#複数ワークツリーの検証)に従う。

必要な検証・独立レビューとbranch内commitを終えた単位から、全体ルートへ版・変更と他単位影響・検証根拠・独立レビュー結果・未確認と残引継ぎ・後片付けを返す。全体ルートは最新devとの差と単位間の接続を確認し、一件ずつFFする。必要な作業branch側rebaseと新版の検証・影響レビュー、同版FF直後のFunctional重複回避、最新devから次branchへ切り替えるWT再利用、統合済みかつ全WT未使用の完了localbranch削除は[統合手順](../spec/development/release.md#通常の作業ブランチの統合)に従う。

| 単位 | 修正対象・根拠 | 採用する境界・対象外 | 完了を判定する代表ケース・既存検証の起点 |
| --- | --- | --- | --- |
| 1 | Everything資源ディレクトリの誤別名化。`assets.ogg/a.wav` のdirectory prefixへファイル拡張子別名化が適用される。 | directory正規化と末尾ファイル別名化を分け、既存prefix cache・種類別hash・自己/祖先索引・packed ABIを維持。物理fallbackで隠さない。 | 既存の完全取得処理を維持して同C++を編集。native/managedの同一キー、入れ子・直下・通常別名、健康度への実接続。通常入力と正しいキーが増える入力の完了時間を分けて比較。 |
| 2 | Everythingのドライブルート縮約。`EverythingBridge.cpp` の末尾整形・フルパス分割が `C:\chart.bms` のdirectoryを `C:` とし、cache照会や候補正規化で正当なrootを失う。 | 方針案: nativeの分割・末尾整形・所有祖先探索・包含判定からmanaged materialize・資源逆引き・推定候補まで、既存のroot保持と同じdirectory同一性へ揃える。packed ABI、prefix cache、種類別hash、走査完全性・完全取得失敗契約を維持。設定/Updater/手動validationの再計上、全パス基盤の置換、実誤配置・破損の断定は対象外。 | 判定案: `EverythingNativeRuntimeTests`、`DirectoryResourceLookupCacheTests`、推定系列へ分担。drive current directoryがroot以外でもnative/managedとhash/資源逆引き候補が同じrootへ届く。通常directory・UNC share root・末尾separator・所有祖先/自己索引を確認。同C++の資源directory別名化後に別コミットとし、隔離入力を使い配布DLL整合も確認。 |
| 3 | 単体分割後の子作品発見漏れ。`BmsLibraryPackageInstallService` は直下の複数譜面を同梱資源なし・共通率閾値未満で単体へ分割すると、排他的な子再帰へ進まず資源付き子作品を発見しない。 | 方針案: directory全体package採用と直下file分割を区別し、分割時は既存の子再帰を続行。全体packageでは包含済み子の重複生成を防ぎ、既存セッション・除外・regroup・親cleanup保護を使う。新グルーピング、発見cache、全ライブラリ走査、保留永続方式変更、親子の無条件再統合は対象外。 | 判定案: 異なる未同梱資源を参照する直下a/b単体＋同梱資源付き子cを、実folder/ZIPから各一回導入または保留へ残す。発見系列と `BmsLibraryManagedArchiveInstallTests` へ分担し、実FS・song/install DB・表示を代表接続。直下1件/資源あり/共通率閾値以上の全体package、孫の再分割、複数子・重複dropと後続sourceの親cleanup保全を維持。発見済み子の推定座標修正とは別コミット。 |
| 4 | 子譜面パッケージの導入先推定。`PackageInstallEstimationSnapshot` の未配置集約をroot相対逆引きへ引き、最終判定前に親候補を失う。 | 候補抽出・同梱充足・足切り・順位を最終配置と同じ座標へ統一。譜面別の既存相対配置を使い、元参照を保持。単独譜面、既所持hash、親参照非対応、音源閾値を維持し、追加I/O・全件走査をしない。 | `BmsLibraryInstallEstimationServiceTests` で通常発見したroot＋子譜面パッケージから推定。候補側/同梱側の正しい子位置・誤った親位置・兄弟同名資源を区別。 |
| 5 | 重複グループの選択識別。見出しは一意でなく、`NormalizeDuplicateViewParameter` と `TryResolveDuplicateSource` が最初の一致・全件へ広げる。 | 上記の手動・自動選択契約へ統一。物理削除先まで誤ることは未確認。 | 同名・空名、未解決、明示root、二フォルダ/同一フォルダ整理後の次対象、三以上の存続対象を通常重複操作で判定。 |
| 6 | 初回重複解析後の古い画面切替。`RegularChartListOwner` のTask完了が後発移動後も元modeを通知する。 | 上記の最終受付優先。重複グループの識別と同じ通知経路を直列で扱い、F01の選択維持へ広げない。 | `RegularChartNavigationTests` 等で受付→別移動→解析完了を順序制御。通常/保守/導入/履歴/playlistの最終mode・parameter・行・左選択を保ち、cacheと必須通知は完了。時間待ちだけで再現しない。 |
| 7 | URL取得の大小文字同一視。成功/失敗集合等がHTTPパス・queryやDrive/MediaFireの不透明IDをIgnoreCaseで潰す。 | scheme/host正規化、fragment除去、同じ共有ファイルの重複抑止を保ち、有意なパス/query/IDは区別。成功/失敗/循環/非CORE除外のキー意味を揃える。MD5・Windowsパスの比較を変えない。 | `PlaylistUrlAcquisitionOwnershipTests`、`PlaylistExternalPackageLookupServiceTests` で別内容が両方保存・一つの導入引渡しへ進む。同一実体抑止、先行失敗後の別キー取得。追加通信・再試行は作らない。 |
| 8a | URL補完元の大小文字同一視。設定は新URLを保存するが、`BMSPlaylist.UrlCompletion` の取得前cache比較が旧データを返す。 | URL補完候補の比較と共通の小さなURI比較へ統一し、二つのprivate比較を置換。HTTPの有意差、scheme/host同値、Windows file URIの既存同一性を保つ。強制毎回取得・新cacheは不要。 | `PlaylistUrlCompletionTests` で大小文字だけ違う二つの固定取得元を実取得し、新実効値まで確認。同一元のcache、失敗時旧候補、空解除、TSV優先を保持。 |
| 8b | URL補完候補の大小文字同一視。`BMSTableEntry.UrlCompletion` が取得済み候補の代入前に同値として戻り、実効値・差分が旧値のままになる。 | URL補完の取得元比較と一コミットだが別判定。URL1/2の有意差をruntime・正規差分へ反映。通常補完はDBへ材料化せず、永続URL優先/上書き設定と明示保存時の材料化を保持。 | 取得元を別名にして取得元比較を回避し、候補の `Song.zip`→`song.zip` とquery差でURL1/2・実効値・receipt・表示行を判定。host/scheme同値で不要な変更なし。 |
| 9 | 転送アーカイブの保存名。`ResolveDownloadedArchiveFileName` は元URLにqueryがある場合しか最終応答URIを使わない。 | Content-Disposition優先で有効な最終名を採用。最終名が無効で元名が有効な既存ケースを保つ。拡張子推測・追加通信・HTML再帰探索は不要。 | `/download/123`→`/song.zip`、ヘッダー優先、最終名無効を通常URL取得の保存・導入引渡しまで判定。URL補完と同じworkflowを直列編集。 |
| 10 | 外部表の転送後相対参照。`PlaylistExternalSyncOwner.GetExternalContentAsync` が最終応答URIを捨てる。 | 取得した各文書の実URIへ相対header/data/entryを解決し、返却表・保存後・公開data基準を揃える。利用者の同期元は一時転送先へ置換しない。回数・制限・取消・解放・失敗時既存表保持を維持。 | slash補完転送・header転送から実dataとentry URLへ到達し、DB再読込み後も同じ意味。リダイレクト後の取得名とHTTP基盤を共有しても別単位。 |
| 11 | JSON出力のdata参照。header/data別ディレクトリを受理するが `PlaylistExport` がbasenameへ落とす。 | 出力時だけheaderから実dataへの正しいURIを作る。既存非空 `Data_url` と正本を保つ。別ディレクトリを新規拒否しない。 | `PlaylistJsonExportDialogTests` 等で同別ディレクトリ、予約文字、異ドライブのfile URIを実出力→参照解決。参照成立と公開時の可搬性を混同しない。 |
| 12 | 数値・日時の地域依存。外部JSON LEVELの既存原因を含み、BMSの小数、ENTRY LEVELの部分抽出、JSON日時・任意文字列、DB読戻し、自然順・表示で地域によって意味が変わる。 | [横断契約](#数値日時の地域非依存契約)へ統一。全体CurrentCulture固定、DB型変更・全件移行はせず、型・精度・時刻帯と形式ごとの文法を保つ。現行仕様・期待値は実装時に同じ契約へ更新する。 | 同じ入力・値をja/en/de/frと非Gregorian文化で解析→保存→新接続読戻し→再出力し、固定表示・検索・整列を確認。実セル・DatePickerの代表を分担する。外部LEVELの明示再読込みがhashだけで抑制されないかも確認。 |
| 13 | BMTコース制約往復。`BmtTableExportService` が短縮gauge名だけを認識し、自身の正規出力名を次回に省く。 | GAUGE_5KEYS/7KEYS/9KEYS/24KEYSと既存短縮名を同じ値へ変換。順序・重複除去・未知制約省略を維持。trophy・公開規則は変えない。 | 実gzip→保存course→再出力で4種類、短縮名、他制約・重複を確認。既存exporterVersionで必要再生成へ接続。既存の復元・出力変換境界を使う。 |
| 14 | 内蔵推薦表の保存済みprefix。生成内容が変わるリロードで属性だけを戻し、実folder・明示順は無修飾のまま保存する。 | 未修飾の生成hashを保ち、保存済みprefixを完成folder・順序へ一度投影。共通変換を使い、setter全域副作用・fingerprint拡張・推薦専用MEMO引継ぎは作らない。 | `PlaylistRecommendedTableOwnerTests` で異なる既存A/Bスコア→設定切替→生成hash差のあるリロード→実所属・順序・DB・必要出力。同一入力時の不要保存抑制。 |
| 15 | LR2名前なし所属のsort範囲。抽出は空文字を所属指定とし、sortは空白判定でALLへ広げる。 | ALL=null、名前なし=空文字をsortでも揃える。受理済み空白名を改名・拒否しない。ALLの代表値・スキーマ・索引・ランダム規則は変えない。 | `BmsPlaylistCustomFolderOutputTests` で同hashの名前なし/別所属へ異なるlevel/adddate。実出力#COMMANDをSQLiteへ適用しLEVEL/ADDDATE両方向と別所属除外。設定保存とは独立したSQL出力の修正。 |
| 16 | 順位cache採用後の成績派生値。`BmsLibraryIrService` の高得点更新と `BMSScore.Overwrite` がrank/rateを更新せず、DJ LEVEL表示・検索・内訳が古い。 | 採用境界で得点/notesから既存 `ScoreValueCalculator` に揃え、新規・既存行を統一。低得点・同点、clear/BP/combo、順位・未送信判定を保つ。RATE列の不具合とは扱わず、全score走査・追加通信・本人IR存廃へ広げない。 | `BmsLibraryIrServiceTests` と起動系列で高得点既存/新規/低得点/同点、1000notes・1800点→AAA/90%を独立値で判定。snapshotから検索・集計へ代表接続。 |
| 17 | 一時停止中の先頭戻し。LR2 Restartはprocessを開始する一方、owner表示はPAUSEで次の操作も逆転する。 | 利用者合意どおりPAUSEを維持し、次の再生で先頭開始。LR2 paused Restartはprocessを開始しない。再生中Restart、現在曲・実player・callbackを保ち、大きな状態機械を追加しない。 | 実LR2 adapter＋process代替でStart→Pause→Restart→Resume→Pauseの表示と開始/終了を対で判定。BMIIDXはPauseがキー送信なので同契約への整合を別確認。次回方式予約・停止中も保持。 |
| 18 | 一時試聴の入力元名衝突。入力元の既存兄弟 `chart_.bms` を見ずに改名し、初回試聴が失敗する。非上書きなので原データ破壊とは分類しない。 | 入力元と同相対先の双方で候補を確認して一回だけ非上書き移動。候補ごとの改名を削除。既存 `_` 命名・相対配置・元名復元・素材借用と主/cleanup失敗を保つ。 | `PlaybackPanelViewModelTests` で通常発見した複数譜面directory package、入力元だけ/両側複数/ディレクトリ占有。元・既存先bytes保全、開始後回収と再試聴。 |
| 19 | 一時試聴の再開・再起動。初回回収後のLR2 Resume/Restart・BMIIDX Restartが回収済みパスを直接使う。 | 一時停止と一時名衝突の修正後、初回と再読込みを同じ一時配置所有処理へ統合。現在曲・実playerの必要文脈だけを保持し、選択や次回factoryから再解決しない。副作用前に必要Lを非待機取得、Busyは状態/process不変。開始・cleanup Taskを既存寿命へ接続。曲全体で一時物・Lを保持しない。 | 実保留→導入先編集→adapter＋fake processで各起動時のファイル存在/内容と再回収。別選択、次回方式予約、Busy、停止/終了も確認。命令列だけのfake playerで代用しない。 |
| 20a | 前曲探索の方向。`Previous` が減算しても `FindPlayableChart` の除外・開始失敗後はNextへ進む。 | 実前曲探索は除外・InvalidDataException後も後方向。Next・先読み、単クリックRestart、repeat、候補なし・明示行開始を保ち、新しい折返しを追加しない。20bと同じ単位で共有ownerを直す。 | 外部playerでA/BMSON/CのCから前曲、後退候補の開始失敗、端・候補なしを独立判定。旧一時停止・一時配置の着手順を先行条件にせず、実際に変えるAPI・gateの依存を確認する。 |
| 20b | 前曲探索の非終端。一覧縮小やStopで現在indexが変わるとPreviousの巡回出口を失い、再生gate待ちの後続操作・終了も止まる。 | 開始時の位置・件数・モードを捕捉し一巡以内で終了する。有限回で既存世代判定へ戻り、失効要求から再生を始めない。検索禁止・全一覧複製・新しい折返し規則は追加しない。 | `PlaybackPanelViewModelTests` の本番Previousで候補評価間へ5→3行の縮小/Stopを順序制御して挿入。元要求、次の明示操作、`CloseForShutdown`の終端を判定。固定一覧後退・別フォルダ・全件同フォルダ・Repeat OFF先頭も維持し、方向不具合とは別に合否を出す。 |
| 21 | 右クリック後の現在セル。行Bへ選択を変えても `CustomTableView.currentCellHit` がAに残る。 | 入力時点で行とセルを揃え、編集時だけ補正しない。集合保持・Shift基点・同じ行の別列を維持。Ctrl解除では既存SelectionModelの現在行へセルを揃え、未選択なら解除。 | 左A→右B→F2/文字/単一セルコピー、複数選択内・同じ行別列・Ctrl解除。F01の識別追従へ広げない。 |
| 22 | 範囲選択の主行通知漏れ。全選択後のShift操作で集合不変だと主行変更を通知せず、停止時ヘッダーが古い。 | 共通選択モデルの集合または主行の変化へ統一。player側補正や追加通知を作らず、同集合・同主行は無通知、基点・再生曲優先を維持。 | `CustomTableSelectionModelTests` と実接続でCtrl+A→Shift末尾/Down、主行変化一回通知、停止中表示更新、再生中現曲維持。右クリックの現在セルとは別原因として判定。 |
| 23 | プロパティPage URL空欄。WPFの空文字→nullがsetterで失敗し、表示だけ空・旧値残存となる。 | nullによる明示解除と非null検証を区別し、無効入力を空へ読み替えない。一般・フォルダ・出力設定の全面作り直しはしない。 | 実ダイアログ→保存→再開で解除と不正入力、header/dataを含む保存経路を確認。限定Binding probeを通常編集session・DB保存の保証にしない。 |
| 24 | 外部プログラム引数の二重引用符。`ExternalProgramArgumentTemplate` が引用符内のペアを反転だけで処理し、A"BをABにする。 | 現行Windows引用規則へtokenizerを揃える。空引数・placeholder後置換・単一引用符・backslash偶奇・不均衡拒否を維持。shell/生Argumentsへ切り替えない。 | `ExternalProgramArgumentTemplateTests` で `--label "A""B" "{filePath}"`、連続ペア、空、偶奇を独立token列で判定し、既存workflowの起動要求へ一例接続。 |
| 25 | Updaterドライブルート正規化。`Program.cs` が絶対化前に末尾slashを落としてD:とし、作業場所基準で解決してready前に失敗する。 | 絶対化後にrootを保つ末尾整形、包含判定・URI基点を整合。書込み可能な既存root配置を拒否せず、ready/proceed・管理領域・長パス・UNC・reparse拒否・rollbackを維持。種類変更修正は任意。 | `UpdaterPackageSyncTests`、`UpdaterProcessGatewayTests` の実プロセス境界で隔離した仮想ドライブ等のroot配置→ready→適用/rollback。通常実ドライブ直下へ試験物を置かず、正規化単体だけで完了にしない。 |
| 26 | 録音形式の偽表示。`EncoderIndex` getterが実行ファイル欠損時に保存値を変えずWAVEを返す。 | 常に保存済み形式を表示し、明示WAVE選択は通常保存。利用不能な別形式の警告・保存拒否、実変換時の既存代替規則は維持。 | 保存済みMP3・encoder欠損でもMP3表示。明示WAVEの保存/再表示/変換snapshot一致、別の利用不能形式拒否後も保存・表示一致。実設定ページの接続を確認。 |
| 27 | 履歴プリセット確定名。ストアはTrimするがeditorへ未正規化名を戻し、次の編集・削除の照合が外れる。 | 確定する一件を正規化し、保存とeditorで同じ内容を使う。新ID・保存形式は不要。 | ` 練習 ` 保存後に再表示なしで対象変更・改名・削除、JSONと表示が同じ一件。取消・空名・重複名を維持。 |
| 28 | 追加出力先の改名入力。setterの毎回TrimがPropertyChanged Bindingへ戻り、途中の空白を消す。 | 入力文字列は保持し、正規化は確定時だけ。保存経路整理とは独立したBinding修正。 | 実Bindingで `My`→`My `→`My Charts`、前後空白の確定時除去、空白名拒否、改名後の出力先・保存値・表参照一致。 |
| 29a | 登録子ルートを含む祖先の改名。LibraryFolderMoveCoordinatorと自動改名の完全一致保護では、空の登録子rootを含む親を改名でき登録が旧pathに残る。 | 既存の登録範囲保護を手動/自動改名へ揃え、登録本体・祖先・既存導入先保護を祖先集約より前に適用する。独立した改名可能対象は処理し、登録自動付替えはしない。29bと一単位。 | `BmsLibraryFolderRenameRefreshTests` で本体/空の登録子を含む祖先/導入先、保護対象と独立対象の混在、同prefix兄弟を判定。FS・DB・設定と次回再読込みの一致を確認。 |
| 29b | 登録範囲を消費する重複統合。統合準備に保護がなく、MergeOwnedSourceContentsが登録元を消費して登録だけ残す。 | 登録BMSルート本体・登録ルートを含む祖先を移動元にする統合を準備段階で拒否し、通常作品同士の統合は維持。登録解除/付替えはしない。既存再生停止と準備の順序を確認し、停止前拒否まで保証を強めない。 | `BmsLibraryDuplicateServiceTests` と `DuplicateMaintenanceWorkflowOwnerTests` で登録本体/登録子を含む元範囲の拒否、通常作品統合の成功、音源・譜面・登録・次回再読込みを判定。改名保護とは独立に合否を出す。 |
| 30 | 保留解除時の再生元回収。PackageCatalogWorkflowOwnerには停止portがなく、導入先空の管理入力を内蔵で背景読込み/先読み中にも削除・全解除できる。 | 管理入力元の物理回収を伴う操作を、同じ既存受付内の再生停止・開始済みデコードjoinへ接続し、成功後だけcleanupする。停止失敗は元を残す。借用入力の記録解除・導入済み記録だけの解除まで無関係に停止しない。 | `PackageCatalogWorkflowOwnerTests`、`BmsLibraryPackageInstallServiceTests` と既存再生変更系列で現在曲/先読みの開始済み読込みを制御し、停止/join→削除の順と失敗時保持を確認。完了後・Pause中の状態収束と借用入力の保全も確認。内蔵の読込み後Restartまで同じ失敗とは断定しない。 |
| 31 | 保留導入先の候補・配置文脈の旧path残存。確定DSTだけの参照抽出と旧候補を保持するMutationFactsでは祖先索引更新だけで直らない。 | 成功した改名/純移動の旧新pathを候補だけのentryとResourcePlacementへ反映し、候補順・未確定・警告・対象外と健康度を維持。削除・統合では影響する未確定候補・配置文脈を失効して明示再推定へ戻し、確定DSTは統合時に従来どおり統合先へ追従し、フォルダ全体削除時は物理削除成功範囲内だけ従来どおり解除する。自動確定/再推定や通常DST編集APIの横断副作用は追加しない。 | 実推定の保留を改名/別root移動→新候補選択し新pathへ確定。`BmsLibraryFolderRenameRefreshTests`、`BmsLibraryLibraryFileOperationsServiceTests`、`MainWindowPackageMaintenanceWpfTests.PendingEstimationAndCandidateEditingReachRealizedRowsWithRelatedEvidence`でDST空/確定、配下/共通prefix、順序/警告、統合先への確定DST追従、影響分だけの失効と明示再推定を確認。 |
| 32 | 保護ファイルの計画内採番衝突。採番helperは実FSだけを見て後続通常宛先を奪い、後段の重複拒否で正常な導入が止まる。 | 短命な一物理計画で全通常宛先・譜面宛先・生成済み採番先を考慮する。既存suffix、同一内容省略と通常上書きを維持。処理済み名だけの予約や永続台帳は作らない。 | `BmsLibraryPackageInstallServiceTests` の実FS/DBで異内容readme.txt/readme(1).txtと既存readme.txt、両列挙順、複数suffix/採番名を確認。txt/bmx/pmx、同一内容省略、導入・重複統合の保持、所持追加・保留解除まで判定。現状の準備失敗を破損と扱わない。 |
| 33 | 再起動後のBMT親MD5欠落。hydrateが生値を遅延保持する一方、Org_md5 getterとBMT projectionが未解決リストを直読する。 | 親MD5の公開読取りを一つの遅延解決へ揃え、backing fieldで再入を避ける。clone/JSONも同値にし、起動時全件展開・出力ごとのDB再読込みは追加しない。旧誤出力は既存exporterVersion/skip境界で一度再生成する。 | `BmtTableExportServiceTests` と実SQLite hydrateで保存→再読込み→事前getterなし→実gzipの親MD5を判定。SHA256のみ/MD5あり、単複数親/親なし、詳細表示有無、元DB保持、旧manifestから再生成→次回省略を確認。コース制約往復と版更新を直列調整。 |
| 34 | 削除履歴への明示追加・移動で生存所属が消える。EntryCommitのidentity索引が削除履歴を生存重複と扱い、Workspaceは元所属削除だけをDBへ確定し得る。 | 局所計画で生存重複と削除履歴を区別し、復帰/置換と移動元削除を同トランザクションへ含める。同identityの履歴・生存行を無条件併存させない。所持一覧から同所属へ明示再追加は新規MEMO・新規追加日時を作る。詳細からの移動/コピーは現在の元行のMEMO・コメント・永続URL・level等を使い、生存重複先の既存優先は維持。 | `PlaylistWorkspacePersistenceCommandTests` で実再同期によりH@B削除履歴/K@B生存→同期OFF→所持からBへ再追加、H@A→B移動/別表詳細コピー→新DB接続と必要出力でBの生存H一件と編集値を確認。直接再追加は旧MEMO/旧日時を復帰しない。元だけ削除されないこと、同folder再drop・生存重複優先・無関係履歴保持を判定。空名drop/一般再読込みMEMO引継ぎとは別原因。 |
| 35 | 再生開始後の反復フォーカス。MainWindowの100ms×10復帰が後発の検索/編集を奪い、BlurやLostKeyboardFocus確定を起こす。 | 開始時の意図した復帰を保ち、成功後・後発明示入力・外部window移動・終了で古い短命要求を終える。無条件反復の削除を優先し、入力禁止・待機延長・永続状態や外部player固有復帰機能の拡充はしない。 | `MainWindowPlaybackWpfTests` と実検索/セル編集で開始→初回復帰→明示入力→後続callbackを制御し、focus・候補文字列・未確定状態の保持を確認。入力なし初回復帰、終了/再接続後無作用も確認。Contentを別hostへ移すfixtureだけで製品HWND/前面判定を代替しない。 |
| 36 | フォルダ編集取消で表示Binding消失。EditableTextBlockのText SetValue/Escape/内部TextBox書戻しがOneWay DisplayNameを外し、recycling後に旧名を残す。 | 表示Bindingと短命な編集候補を分け、確定時だけ現在対象・候補を既存改名入口へ一度渡す。取消/対象交換は候補を捨てる。EditModeChangedとMainWindowの確定値取得、DPの開始/終端を同じ契約へ揃える。Escape一行だけの補正、DisplayName TwoWay化、仮想化無効化はしない。 | `MainWindowPlaylistWorkspaceWpfTests` の実ツリー/compiled部品で無変更Escape・変更Escape・正常確定。Aの選択/focusを外して実scrollで同一部品がCへ再利用されたこと、Binding・新DataContext・表示名・操作対象を一組で確認。取消は保存なし、確定は一回。 |
| 37 | ready書込み中の公開。最終名へ直接書くwriterと存在直後のReadAllTextが共有違反になり正常な更新開始を失敗させる。 | 同currentの一時名へmarkerをflush/closeし、完成後に最終ready名へ公開する。既存decisionの公開契約へ揃え、内容・期限・proceed/cancel・失敗時アプリ存続を維持。 | `UpdaterProcessGatewayTests`、`UpdaterPackageSyncTests` で公開準備中は最終名なし、出現時はclose済みを実writer/readerの順序制御で確認。receipt後だけ終了準備し、未公開期限切れ・取消・本来の起動失敗時回収を維持。writer終了後から読むfixtureだけにしない。 |
| 38 | 更新cleanupが所有中の同期lockを削除。journalなし・新版起動直後にUpdater/watchdogの排他保持と重なり誤Failed通知になる。 | update-transaction.lockを一時物回収から除外し、OpenOrCreateで再利用する既存寿命を維持。例外全体抑制・待機再試行は追加しない。 | `UpdateDownloadServiceTests.CleanupPreviousWorkDirectoryPreservesActiveUpdaterPayload` でjournalなし/実lock保持中もlock/current/receiptを保ち、古いdownloadを回収。ready受理→新版起動後cleanupの系列を接続し、破損とは断定しない。 |

## 数値・日時の地域非依存契約

単位12はBMS、JSON・DB・設定、数値セル、一覧の文字列表示、検索・整列、機械出力を対象とする。同じ文字列入力・保存値の意味をOS地域と翻訳言語に依存させない。現行の装飾抽出・地域セル入力・不正日時でも行を保持する保証は、以下の明示変更を維持する条件としない。実装時は[一覧](../spec/ui/table-view.md)、[保存と出力](../spec/playlist/storage-and-export.md)、[設定の保存](../spec/runtime/settings-persistence.md)、[音声](../spec/runtime/audio.md)など該当正本・既存期待値を一緒に更新する。本計画の統合時点で製品へ適用済みとは扱わない。

### 共通の形式と型

| 境界 | 採用契約 |
| --- | --- |
| 数値の表示・通常入力 | 小数点`.`、桁区切りなし。小数セルは符号・指数を含む数値全体、整数項目は固定Cultureの整数文法。空欄・値域・表示精度は項目の型/用途に従い、編集表示は無変更確定で値が変わらない精度にする。 |
| 日付・日時の通常文字列 | ピッカーを除きGregorianの`yyyy-MM-dd`、日時は`yyyy-MM-dd HH:mm:ss`を基本とする。用途ごとの日/分/秒の粒度を保ち、曖昧な月日順・2桁年を通常入力へ追加しない。 |
| JSON数値・任意文字列 | 数値はnumber/nullのまま扱う。任意の名前・タイトル・式を日時へ自動推測しない。DOM/文字列設定readerはDateParseHandling.Noneを基本に日時欄だけ明示解析し、型付きDateTime DTOの正常読戻しまで一律無効化しない。 |
| DB・設定 | 数値のINTEGER/REAL/NULL、日時のticks等と型付きパラメーターを維持。地域推測の文字列fallbackを除き、必要な固定旧形式を明示する。型付き設定シリアライズとSQLite quote()のバックアップを保ち、数値風TEXTの型を変えない。 |
| 時刻の意味 | 日付、ローカル壁時計、絶対時刻を区別して既存意味を保つ。UTC一括変換や壁時計へのZ付与はしない。明示offsetを受ける境界は既存時刻帯契約への変換を明示する。 |
| 文化圏と機械出力 | CurrentCulture/DefaultThreadCurrentCultureを全体固定しない。既存parser/formatter/converterの変換境界へInvariantと形式を集約し、型付き経路に文字列を挟まない。翻訳・初回言語判定を維持し、OS文化の退避状態・独自Culture設定を増やさない。保存/検索式/ハッシュ入力/外部引数/管理名/Updaterプロトコルも呼出しスレッドに依存させない。 |

日付separatorを目的なく変更して既存の固定検索式へ波及させず、共通表示をコピーした値も同じ意味で検索できるようにする。名称・パス・識別子の比較は別契約として維持する。詳細解析のJava互換数値、厳密な有理数、BMSONの生の数値字句、既存の精度・特殊値・互換文法を保持する。BMSの桁区切りを含む従来en-US相当の受理範囲はCulture固定と別判断であり、一律拒否へ変えない。

BMSFileTimingの事前検査から係数・指数・小数点の分解まで同じ規則を使い、基本/拡張BPM、STOP、小節長、TOTAL、PLAYLEVEL、RANK/DIFFICULTY/分岐整数を揃える。StringExt.TryParseOrDefaultの本番用途が整数だけなら不要な反射汎用処理を整理する。NaturalComparerと一覧ソートの文字列数値fallback、Lr2IrRankingCacheParserの固定日時入口、ChartInfoParser.Outputの整数も対象とし、型付きキーを優先する。

### ENTRY LEVELと外部ラベル

ENTRY LEVELはnullable数値の正本を保つ。前後空白を除く全体をNumberStyles.FloatとInvariantで解析し、`1.5`、`-1.5`、`1e3`は1.5、-1.5、1000とする。空白だけはnull、取消は旧値保持。桁区切り・部分抽出・NaN/無限大、`★1.5`の装飾、`1,5`/`12,5`等のカンマは拒否し、保存しない。不正値を先頭数値・0・nullへ読み替えず、既存の編集結果/通知へ明示的に接続する。新たな上下限は設けない。

外部JSONのlevelはFOLDERラベルとして原文を保持する。数値tokenは直接取得し、文字列ラベルから必要な既存抽出だけInvariantで行う。非数値ラベルを禁止せず、org_level優先・明示null、元folder、数値なし/複数数値の既存扱いを保つ。数値セルの全体解析と外部ラベルの文法を混同しない。

### JSON日時の保存と旧入力

| 項目 | 新規出力の固定形式 | 保持する意味 |
| --- | --- | --- |
| output_date | `yyyy-MM-dd` | 出力した日。 |
| last_update | `yyyy-MM-dd'T'HH:mm:ss.fffffff` | 現在の壁時計日時と1tickの粒度。 |
| adddate | `yyyy-MM-dd'T'HH:mm:ss.fffffff` | 保存済み追加日時と1tickの粒度。 |

壁時計へoffsetを付けず、DB読戻しのLocal/Unspecified差で文字列が変わる単純なO形式への置換はしない。既存の固定旧形式はCultureを明示して読み、存在する非空の日時欄が受理する固定形式へ適合しなければ、取込み/再読込み操作全体を保存前に止める。空欄・null・省略は許容する。`01/02/2026`等を地域・試行順で推測せず、対象項目と要求形式を示し、元データ訂正後の再実行を求める。既存の保存値を保ち、今日・null・既定日時への代替、行だけの省略、新しい移行設定/画面は追加しない。

右クリック設定のWeb項目名`2026-10-26T13:24:35`、表/コース/folder/譜面名などは文字列のまま往復させる。プレイリストreaderに加え、BMTのコース再解析・cache・外部設定の読戻しも型保持へ揃える。DB型変更・全件移行・全DB自動補正はしない。

### 一覧と型付き日付部品

CustomTableColumn、ENTRY LEVEL投影、ランプビューア、プレイログ、共通converterと必要なBindingへ固定Culture/書式を接続し、件数の桁区切りを含め上記方針へ揃える。割合等の用途ごとの精度は保ち、表示変更で再解釈・再保存しない。

DatePickerは標準Calendar、読取り専用Text、既存ResourceService.DisplayCultureの地域表示を維持する。月名・曜日名・日付順・週開始を固定しない。SelectedDate→SelectedAsOfDate→集計条件のDateTime?を型のまま渡し、既存.Date/Unspecifiedを保持する。アプリが表示Textを再解析したりUTC化したりしない。標準部品内部のText解析と表示Culture/暦/書式は一致させ、見かけだけ別書式で上書きしない。Loaded/再テンプレート/再有効化後も表示接続・IsReadOnlyを維持する。

### 横断単位の代表判定

| 境界 | 判定と既存検証の起点 |
| --- | --- |
| 同一入力・異文化 | ja-JP/en-US/de-DE/fr-FRと非Gregorianのth-TH等へ同じ入力・値を渡し、翻訳言語は別に変更する。境界へ直接異なるCurrentCultureで入っても同じ結果にする。 |
| BMS | 同一bytesでBPM/TOTAL/PLAYLEVEL/STOP/小節長/イベント時刻が一致。RibbitBmsFileTimingTestsの地域別入力期待を同一入力へ更新し、文法・精度・特殊値は保つ。 |
| 数値セル・外部level | PlaylistEntryCommitTests、MainWindowPlaylistWorkspaceWpfTests、PlaylistUrlCompletionTestsとBMSTableLoadTestsで、1.5表示→F2→無変更Enter、2.5/-1.5/1e3、Escape/空欄/装飾/カンマを実セルから新DB接続・再表示・JSON往復まで確認。外部ラベルとorg_levelの型/優先を別に判定する。 |
| JSON日時・任意文字列 | BMSTableLoadTestsで異文化読込→保存→再読込→出力の数値/nullと日時7桁を保持。不適合非空日時で操作全体の保存前停止・旧状態保持、訂正後成功、空/null/省略許容を確認。右クリック設定実保存とBmtTableExportServiceTestsの実gzip往復で日時風の名前を保持する。 |
| DB・設定・機械出力 | 実repository/provider/バックアップで値/NULL/ticksと数値風TEXTの型を保持し、設定の小数・ChartString/charthash・外部引数・管理名・更新protocolが一致。不要なスキーマ変更と全体Culture固定がない。 |
| 表示・検索・整列 | 編集初期値の無変更確定で値が変わらず、表示した数値/日時を同じ意味で検索し、1.5と2等の自然順が一定。既存固定日付検索式と壁時計意味を保つ。 |
| 型付きピッカー | PlaylistLampViewerWindowPresentationTestsの実部品で地域表示差を許し、同じ日が同じ型付き値/集計条件へ届く。言語変更・LostFocus・Popup開閉・再有効化/再テンプレートで選択日・表示月・表示モード・取得要求が変わらず読取り専用も保つ。既存地域表示期待を一律Invariantへ変えない。 |

## 責務別の参照と依存

| 領域 | 正本・共有境界 |
| --- | --- |
| 設定・保存・セル | [設定](../spec/runtime/settings.md)、[設定の保存](../spec/runtime/settings-persistence.md)、[一覧](../spec/ui/table-view.md)、[保存と出力](../spec/playlist/storage-and-export.md)。設定ViewModel/Windowへの書込みは直列。数値・日時の将来契約は上記横断単位へ揃え、外部ラベル抽出と数値セル全体解析を分ける。score・BMTの失敗契約はそれぞれ独立。 |
| 物理変更・資源・推定 | [ライブラリ変更](../spec/library/mutations.md)、[整合性](../spec/library/file-db-consistency.md)、[重複](../spec/library/duplicate-files.md)、[導入](../spec/library/drop-install.md)、[推定](../spec/library/install-estimation.md)、[性能](../spec/core/performance-and-scale.md)。登録範囲保護と保留参照の成功factsが使う共有パスの変更は直列化する。nativeの資源directory別名化とroot保持は同C++を直列編集し、既存の完全取得と走査完全性を保つ。単体分割後の子発見・発見済み子の推定は同じ導入経路で別原因として判定する。 |
| 表・URL・履歴 | [取得](../spec/playlist/downloads.md)、[Table URL取込み](../spec/playlist/table-url-import.md)、[BMT](../spec/playlist/bmt-export.md)、[LR2出力](../spec/playlist/lr2-custom-folders.md)、[履歴](../spec/playlist/play-history.md)。URL比較の共有とcache・取得所有者の結合は別判断。BMT混在名、推薦MEMO、本人IR存廃は任意。 |
| 再生・画面・外部起動 | [再生パネル](../spec/ui/playback-panel.md)、[一覧](../spec/ui/table-view.md)、[外部起動](../spec/ui/external-launch.md)。前曲の有限化を最優先にし、PAUSE中の起動要否・候補名決定・再開始は実共有依存だけを直列にして共通ownerへ接続。右クリックと範囲選択の原因を区別。 |
| 更新 | [ポータブル更新](../spec/integration/portable-update.md)。root正規化、ready完成後公開、同期lock保持を別原因の必須単位とし、管理file→directory遷移の実装は混ぜない。 |

資源ディレクトリ別名化の調査で参照したLinux上の抽出索引コードの計測は、Windowsの製品bridge・DB/UIの完了時間の非退行を保証しない。通常入力と正規キーが増える入力の仕事量・完了時間を分け、未測定の条件は制約として明記する。恣意的な許容率や無関係な全体ベンチマークを追加しない。

共有表コンテキストメニューの対象解決は、PlacementTargetとMenuItem.DataContextの参照元が異なるという指摘だけでは別表操作を実証できず、必須改修に数えない。限定WPF probeのA→B→Aでは一致した。確認する場合は実MainWindowで複数表を開閉し、現在対象と処理先を判定する。明示DataContext代入だけのテストで保証せず、乖離を再現した場合だけ対象解決の修正を再判断する。新cacheやメニュー全体の書換えを先行しない。

## 実装開始・検証・完了条件

各単位の着手前に、[テスト設計](../spec/development/test-authoring.md)で不足する恒久保証だけを独立設計し、単位責任者の承認と[引継ぎ点検](../spec/development/agent-workflow.md#引継ぎの点検)を経て着手する。既存の保存段階省略、旧要求固定、旧スコア保持、候補完了引継ぎを正解の根拠にしない。追加対象も修正案・受入例からそのままテスト数や新保証を確定せず、必要な独立設計と引継ぎ点検を経る。置換・削除するテストは保証の移管先または不要な理由を明確にする。

判定条件は既存サービス・実SQLite・実FS・native等へ置き、WPFは実Binding・イベント接続の代表へ分担する。新規テスト不要の判断と既存回帰の実行範囲を区別する。最新入力での必要媒体再保存、入力修正後の成功、未知設定・管理登録・無関係候補の保持、部分保存の失敗通知、通常閉鎖・再表示・終了joinも既存保証として残す。

各単位の反復は `scripts/verify-refactor.ps1` の対象を絞った `Quick`、通常開発のコミット・統合前は単位責任者が全体ルートと開始調整して `Functional`、公開候補は `Full` を選ぶ。[検証仕様](../spec/development/testing.md)の環境・実行回数・失敗分類に従う。native変更は生成物と配布整合も確認する。

保存・削除・互換性、受付・Task寿命、索引・走査完全性、主体の責務を実質変更する単位は重大変更として扱う。各最終差分で分類し、凍結した新規コンテキストの予備レビュー後、独立した通常または重大最終レビューへ進む。重要度ラベルだけでレビュー種別を選ばない。

通常入口の必要な再現と修正後の結果、維持する取消・失敗・保存・表示・性能を確認し、必須検証と最終レビューを通過して完了とする。必要な契約・実装・テスト対応を機能仕様へ移し、完了項目は本書から削除する。全項目が完了したら本書と不要な参照を削除し、[計画一覧](README.md)を更新する。
