# リリース前の必須修正

## 目的と採用範囲

通常の入力・操作で到達する保存、導入、一覧、再生、出力の不整合を解消する。本書は採用済みの契約と未完了の実装・検証を扱う。対象は39件、実装のコミット単位は下表の38単位とする。URL補完の取得元比較と候補比較を一単位にまとめるが、二つの不具合の判定は独立して残す。[任意改善](release-follow-up-candidates.md)は今回実装せず、リリース完了条件に含めない。

追加指摘は `dev` の `8c53fc9b` とLR2出力簡素化の作業差分を照合した静的調査を根拠とする。既存対象も含め、通常経路の確認を実再現・テスト成功と読み替えない。着手時に最新のコードと既存差分を確認し、解消済みの課題を再実装しない。LR2出力設定の保存・生成の契約は[設定の保存](../spec/runtime/settings-persistence.md)と[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)を参照する。

以下の代表判定は独立テスト設計の代替ではなく、実装・テストが完了した保証でもない。

## 共通の変更境界

- 現行の入力受理、取消、受付、成功対象の一変更セッション、管理外ファイル・URLの保全を維持する。入力拒否の追加や意味の変わる代替は、合意した範囲を超えて導入しない。
- 設定は自身の候補と他項目の最新確定値を使う。受理済み入力の固定、候補排他、設定実操作の同時一件、必要なL/P受付、終了時の開始済みTask回収を維持する。通常閉鎖は候補だけを捨て、再表示は公開済み確定値から作る。
- 過去の失敗要求・成功段階を保持して後続操作を制限する方式を、最新入力の保存と保存済み正本からの再生成へ置換する。予約、自動再試行、自動巻戻し、永続修復台帳、専用修復管理、差分ごとの常駐完了辞書は追加しない。
- 少数変更の全件走査・全行実体化・全ソートを常用しない。既存の所有主体と通知・Taskを再利用し、旧処理を並存させない。同じファイルを扱うだけの理由で別機能を一括コミットしない。
- 新しい利用者向け文言は `Resources.resx`、`Resources.cs` と6言語辞書を同じ単位で揃える。現行仕様と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)の変更も該当単位に含める。

## 保存・生成・編集で採用する契約

### スコア設定

スコア設定変更は関連項目の保存、必要な読込み、cache失効、snapshot公開、必要表示だけを行う。BMTの出力先解決・保存・生成・旧先整理・URL同期は呼ばず、BMT操作もscore選択不備に依存させない。root変更だけが双方への影響を処理する。

読込み失敗を新設定の結果として旧スコア表示へ置き換えない。旧情報の退避・復元、`scoreReloadPending` による旧要求固定を削除し、失敗後も別の設定を確定できるようにする。旧スコア復元は受入条件にしない。

### BMT出力先とroot変更

異なる物理出力先への変更は、新先の必要生成物・管理情報を準備し、`user.config` の保存成功を新先の採用点とする。その後に管理URLを切り替え、成功した場合にだけ旧先の管理対象を整理する。

- 準備失敗では旧先を採用したまま終了する。採用後のURL更新失敗は新先を維持する部分失敗とし、旧URLの残存とファイルの残存を別に通知する。この操作では旧先のファイル整理へ進まない。
- 同じrootのURL置換は一回の同期にまとめる。別rootは新root登録後に旧rootから解除する。採用後の旧先整理失敗でも新先を維持し、残存を通知して終了する。
- 物理同先の形式変更と明示再更新は現在設定・保存済み正本から再生成する。部分生成物の完全復元や過去要求の正確な再開は保証しない。管理外ファイル・管理外URLを保つ。
- BMTをOFFにするときの「既存ファイルを保持」は、既存ファイル・管理情報・管理URLを保持する。出力先変更時の切替・整理とは別の明示的な保持方針である。
- `pendingBmtChange`、成功段階、旧先整理の先行、過去要求だけを理由とする変更・復元・再起動拒否を削除する。旧要求から後日旧先を自動回収する保証を設けない。root採用後の必要score処理をBMT失敗だけで飛ばさない。

### 一覧セル編集

詳細セル、表一覧属性、譜面フォルダ名、保留導入先の確定は、保存、必要出力、確定値の必要表示反映、編集状態解除、受付解放の順にする。入力欄が閉じても非同期保存の完了とは扱わない。保存後の出力失敗でも必要表示と編集解除を行い、元の失敗を通知する。

既存の単一consumer・変更配送Task・UI実適用Taskと局所投影を操作所有Taskへ接続する。表示交換後の旧表示へ反映を要求せず、開始済み処理とcleanupは回収する。後発の全ソート・全体再構築の完了を新条件にしない。詳細だけの旧source再コピー、表示前の受付解放、Task外への必要表示投稿、receipt別完了管理、構築候補の後継への完了引継ぎは置換・削除対象とする。

## ライブラリ操作で採用する契約

### 登録ルート本体の移動と削除

登録BMSルート本体の移動は拒否して理由を案内し、普通の子曲フォルダの移動は維持する。祖先への集約前に保護対象を考慮し、ルートと子曲の混在選択で子曲まで落とさない。確定した同じ移動計画をP判定、再生停止、物理処理へ渡す。登録の暗黙移行や直下譜面だけの別操作への読み替えは行わない。

全体削除確認には登録ルート本体の削除と登録解除を明示する。Yesで承認され、物理削除が成功した当該ルートだけ登録解除する。ただし最後のルート、または現在の `BMSInstallDir` に該当するルートは、先に既存設定で別ルート・導入先を確定する必要がある。準備前の全体削除は拒否し、既存確認で明示選択する譜面のみ削除は維持する。

成功パスを既存の設定保存主体へ渡し、standaloneとLR2で登録を二重管理しない。物理削除後の登録保存失敗は部分失敗として報告する。自動代替、空ルート設定運用、空フォルダ再生成、更新検査の緩和、修復台帳・自動再試行・全体巻戻しは追加しない。LR2管理rootを通常ユーザールートの解除へ巻き込まない。

### 重複グループの選択と移動要求

手動選択と整理後の自動選択は、現在の検索結果内で一意な所属フォルダによる短命な識別へ揃える。二フォルダ統合・同一フォルダ整理は操作前に捕捉した次のグループ、三フォルダ以上の統合は存続グループを選ぶ。同名・空名でも取り違えず、対象消失は空結果とする。明示した重複ルートだけが全件を表示し、フォルダ単位選択との区別を保つ。見出しの最初の一致という旧経路、永続ID・選択永続化・追加の全件再検索は残さない。

初回重複解析中も許可される後発の画面移動を優先する。入力受付から表示採用まで同じ短命な要求識別を使い、古い画面切替と解析途中のsummary解除等の表示副作用を採用しない。必要なcache構築・catalog通知は完了させる。既存の一覧要求識別・取消を利用できる境界を確認し、画面移動をBusyで新規禁止する案は採らない。

### 祖先資源索引の更新

物理変更に成功した旧新パスから親を辿り、既存索引へ個別照会して影響する祖先だけを集める。更新対象の祖先は重複だけを除き、物理走査rootの包含削減とは分ける。統合・削除の後片付け後の最終配置を既存 `ChartDirectoryScanBuilder.TryBuildFromRoots` で走査し、自己索引と祖先集約を区別して一変更セッション・一索引公開へまとめる。移動部分の既存hash、構造共有、遅延逆引きを保つ。

必要な祖先走査が不完全または失敗した場合は、既存の確定後失敗として通知し、古い索引snapshot・世代を維持する。成功済み物理処理・DBを巻き戻さず、永続修復要求や全カタログ再構築を追加しない。

## 着手順とコミット単位

以下を一つの直列順とする。P1は既存一覧の末尾へ機械的に追加せず先行し、ENTRY LEVELだけは一覧セル編集の終端整理を先行条件にする。表中のクラス名は着手時の探索起点であり、APIや配置を固定するものではない。各単位は実装・必要テスト・仕様・翻訳を含む。

| 順 | 修正対象・根拠 | 採用する境界・対象外 | 完了を判定する代表ケース・既存検証の起点 |
| --- | --- | --- | --- |
| 1 | 管理出力のドライブルート除外漏れ（条件付きP1）。`SettingsDialogViewModel` の包含比較がルートへ区切りを重ね、一般BMS候補・導入先保存へ管理領域を通す。導入後の表削除で物理消失に至り得る。 | 既存 `CustomFolderOutputBaseSearchRootSyncService` と同じ包含意味へ揃え、重複比較を整理する。LR2の必要jukebox登録を保ち、ドライブ/UNCルートを新規拒否しない。 | `SettingDialogCustomFolderOutputBaseTests` でルート、同一・子・接頭辞兄弟、大小文字・末尾区切りの候補と実保存。通常導入先の導入→表削除で楽曲保全。 |
| 2 | Everything SDK取得の切詰め（条件付きP1）。`EverythingBridge.cpp` の3getterは返却書込み長を全文長と誤認し、取得失敗のskipも完全走査へ渡る。既存長パスのDB登録が不在扱いになる。 | 完全文字列だけをsplit/callbackへ渡す。完全取得を確認できない結果を黙示欠落した完全走査にしない。通常短パスの一回取得・query内バッファ再利用を保つ。巨大固定バッファ、最大長の新規拒否、無関係なSDKエラー再設計は対象外。 | 実bridgeで短パス、1022/1023、1024超、サロゲート境界、fullpath/分離取得、同query再利用。走査・差分確定で既存長パスのDB保持。版・hash確認やmanagedだけの長パステストで代用しない。 |
| 3 | BMT復元の非数値所属消失（P1）。`BeatorajaBmtTableImportService` はnull levelの `org_level` を省き、`BMSTableEntry` はfolderを採用せず、同hash別所属が統合され得る。 | cache復元の合成JSONにnullも含む `org_level` を出し、所属とnullable levelを保持する。一般JSONの受理厳格化・DB列・失われた所属の推測修復をしない。混在prefix名の変更は任意。 | `BmtTableExportServiceTests`、`BMSTableLoadTests` で取得失敗→実gzip復元→通常登録→DB→再出力。`★???`、お気に入り、数値混在、同hash別所属を区別。 |
| 4 | 一覧セル編集の表示終端。保存・出力後のUI配送を待たず、確定表示・draft解除より先に受付を解放し得る。 | 上記セル編集契約へ詳細・表一覧属性・フォルダ名・保留導入先を統合する。 `DetailEditing`、`DetailChanges`、`PropertyEditing`、`RegularChartListOwner`、`PendingPackageWorkflowOwner` が起点。 | 保存・出力・必要表示・編集解除の各境界まで受付を保持。出力失敗、表示交換、通常閉鎖・終了でcleanupと元失敗を回収。WPFは実接続の代表に絞る。 |
| 5 | ENTRY LEVELの地域設定往復（P1）。現在文化の表示 `1,5` を `BMSTableEntry` のピリオド部分一致で1として正常保存する。 | 地域設定どおりの表示・編集と従来の `1.5`・`★1.5` を両立。ピリオドが桁区切りの文化でも15へ変えない。空→null、非数値→旧値、取消を維持。桁区切りの新機能・部分一致全面廃止・拒否追加は対象外。 | 4の後。`PlaylistEntryCommitTests` でfr-FR/de-DE/en-USの正負小数と従来装飾入力をDB値で判定。外部同期OFFの実ENTRY LEVELでF2→無変更Enterを代表確認。 |
| 6 | スコア読込みと失敗後の次操作。score-onlyにも旧情報保持・失敗入力固定が残る。 | 上記スコア設定契約へ置換し、BMTからの独立を保つ。 | 設定・score読込みの既存系列で失敗後の別設定確定、旧値を新結果に流用しない表示、cache失効・snapshot・必要表示。BMT未呼出し、通常閉鎖・再表示・終了join。 |
| 7 | BMTの準備・採用・整理とroot変更。旧先整理が新先生成に先行し、BMT失敗で必要score処理も止める。 | 上記BMT契約へ `PlaylistBmtOutputOwner` と設定確定の接続を変更し、旧要求・成功段階を撤去する。 | 準備失敗は旧先、user.config採用後のURL/整理失敗は新先。URL失敗では旧ファイル整理なし。同root/別root、同先再生成、OFF時保持、管理外保全、root採用後score処理を判定。 |
| 8 | 登録ルート本体の移動。`BmsLibraryLibraryFileOperationsService` の外側フォルダ集約に登録判定がない。 | 上記ルート移動保護。物理名でなく設定登録との同一性で判定する。 | `BmsLibraryFolderRenameRefreshTests` 等で登録ルート直下、通常子、混在、後続更新検査。部分失敗・一括通知を保持。 |
| 9 | 登録ルート全体削除後の登録残存。成功フォルダが設定解除へ接続されない。単純解除では最後のルート・導入先の設定契約にも反する。 | 上記削除契約と既存の設定保存主体へ接続。6・7後の保存責務を再利用する。 | `BmsLibraryLibraryFileOperationsServiceTests` と通常削除・重複hash整理の両入口。Yes/No、準備前拒否/準備後成功、譜面のみ、物理失敗・未処理保持、成功対象だけの解除、保存・公開失敗報告。 |
| 10 | 物理変更後の祖先資源索引。`LibraryResourceIndexOwner.UpdateMovedFolderReferences` は移動範囲外の旧新祖先を更新せず、明示再スキャンも古い索引を使う。 | 移動・削除・統合の成功事実を一つの索引責務へ集め、自己索引・逆引き・世代を最終配置へ揃える。関連全譜面の即時再評価や全件走査は追加しない。 | 旧新祖先の資源キーと物理配置、後続の再スキャン、部分成功を既存移動・削除・資源索引系列で判定。8・9と同じ物理成功境界を直列で扱う。 |
| 11 | Everything資源ディレクトリの誤別名化。`assets.ogg/a.wav` のdirectory prefixへファイル拡張子別名化が適用される。 | directory正規化と末尾ファイル別名化を分け、既存prefix cache・種類別hash・自己/祖先索引・packed ABIを維持。物理fallbackで隠さない。 | 2後に同C++を編集。native/managedの同一キー、入れ子・直下・通常別名、健康度への実接続。通常入力と正しいキーが増える入力の完了時間を分けて比較。 |
| 12 | 子譜面パッケージの導入先推定。`PackageInstallEstimationSnapshot` の未配置集約をroot相対逆引きへ引き、最終判定前に親候補を失う。 | 候補抽出・同梱充足・足切り・順位を最終配置と同じ座標へ統一。譜面別の既存相対配置を使い、元参照を保持。単独譜面、既所持hash、親参照非対応、音源閾値を維持し、追加I/O・全件走査をしない。 | `BmsLibraryInstallEstimationServiceTests` で通常発見したroot＋子譜面パッケージから推定。候補側/同梱側の正しい子位置・誤った親位置・兄弟同名資源を区別。 |
| 13 | UTF-8 BOMの表示メタデータ。`BmsEncodingDetector.DecodeBytes` がBOMでUTF-8復号した本文にCP932名を返し、表示補正を誤る。 | 実復号方式と判定名を一致させる。対象はUTF-8 BOMと表示メタデータ。資源名CP932、hash、BOMなし判定、UTF-16/32対応、パーサー版更新を便乗変更しない。 | 実BOM付きBMSの先頭TITLE・日本語を解析し、新規登録・明示保守・LR2同期・パッケージ読取りの代表へ接続。任意のプロパティ解釈表示と分ける。 |
| 14 | 重複グループの選択識別。見出しは一意でなく、`NormalizeDuplicateViewParameter` と `TryResolveDuplicateSource` が最初の一致・全件へ広げる。 | 上記の手動・自動選択契約へ統一。物理削除先まで誤ることは未確認。 | 同名・空名、未解決、明示root、二フォルダ/同一フォルダ整理後の次対象、三以上の存続対象を通常重複操作で判定。 |
| 15 | 初回重複解析後の古い画面切替。`RegularChartListOwner` のTask完了が後発移動後も元modeを通知する。 | 上記の最終受付優先。14の識別と同じ通知経路を直列で扱い、F01の選択維持へ広げない。 | `RegularChartNavigationTests` 等で受付→別移動→解析完了を順序制御。通常/保守/導入/履歴/playlistの最終mode・parameter・行・左選択を保ち、cacheと必須通知は完了。時間待ちだけで再現しない。 |
| 16 | URL取得の大小文字同一視。成功/失敗集合等がHTTPパス・queryやDrive/MediaFireの不透明IDをIgnoreCaseで潰す。 | scheme/host正規化、fragment除去、同じ共有ファイルの重複抑止を保ち、有意なパス/query/IDは区別。成功/失敗/循環/非CORE除外のキー意味を揃える。MD5・Windowsパスの比較を変えない。 | `PlaylistUrlAcquisitionOwnershipTests`、`PlaylistExternalPackageLookupServiceTests` で別内容が両方保存・一つの導入引渡しへ進む。同一実体抑止、先行失敗後の別キー取得。追加通信・再試行は作らない。 |
| 17a | URL補完元の大小文字同一視。設定は新URLを保存するが、`BMSPlaylist.UrlCompletion` の取得前cache比較が旧データを返す。 | 17bと共通の小さなURI比較へ統一し、二つのprivate比較を置換。HTTPの有意差、scheme/host同値、Windows file URIの既存同一性を保つ。強制毎回取得・新cacheは不要。 | `PlaylistUrlCompletionTests` で大小文字だけ違う二つの固定取得元を実取得し、新実効値まで確認。同一元のcache、失敗時旧候補、空解除、TSV優先を保持。 |
| 17b | URL補完候補の大小文字同一視。`BMSTableEntry.UrlCompletion` が取得済み候補の代入前に同値として戻り、実効値・差分が旧値のままになる。 | 17aと一コミットだが別判定。URL1/2の有意差をruntime・正規差分へ反映。通常補完はDBへ材料化せず、永続URL優先/上書き設定と明示保存時の材料化を保持。 | 取得元を別名にして17aを回避し、候補の `Song.zip`→`song.zip` とquery差でURL1/2・実効値・receipt・表示行を判定。host/scheme同値で不要な変更なし。 |
| 18 | 転送アーカイブの保存名。`ResolveDownloadedArchiveFileName` は元URLにqueryがある場合しか最終応答URIを使わない。 | Content-Disposition優先で有効な最終名を採用。最終名が無効で元名が有効な既存ケースを保つ。拡張子推測・追加通信・HTML再帰探索は不要。 | `/download/123`→`/song.zip`、ヘッダー優先、最終名無効を通常URL取得の保存・導入引渡しまで判定。16と同workflowを直列編集。 |
| 19 | 外部表の転送後相対参照。`PlaylistExternalSyncOwner.GetExternalContentAsync` が最終応答URIを捨てる。 | 取得した各文書の実URIへ相対header/data/entryを解決し、返却表・保存後・公開data基準を揃える。利用者の同期元は一時転送先へ置換しない。回数・制限・取消・解放・失敗時既存表保持を維持。 | slash補完転送・header転送から実dataとentry URLへ到達し、DB再読込み後も同じ意味。18とHTTP基盤を共有しても別単位。 |
| 20 | JSON出力のdata参照。header/data別ディレクトリを受理するが `PlaylistExport` がbasenameへ落とす。 | 出力時だけheaderから実dataへの正しいURIを作る。既存非空 `Data_url` と正本を保つ。別ディレクトリを新規拒否しない。 | `PlaylistJsonExportDialogTests` 等で同別ディレクトリ、予約文字、異ドライブのfile URIを実出力→参照解決。参照成立と公開時の可搬性を混同しない。 |
| 21 | 外部JSON LEVELの文化依存。ピリオド抽出文法に文化未指定の `double.TryParse` を使い、通常levelをnull/15等にする。 | 外部levelはInvariantCultureで解析。org_level優先・明示null・元folder・数値なし/複数数値の既存扱いを保つ。5の地域セル入力と別境界。全文化変更・全DB自動補正はしない。 | `BMSTableLoadTests` でcultureを固定した通常level/ org_level→登録→DB値。既存誤値の明示再読込みがhashだけで抑制されないか確認。5と同ファイルを直列編集。 |
| 22 | BMTコース制約往復。`BmtTableExportService` が短縮gauge名だけを認識し、自身の正規出力名を次回に省く。 | GAUGE_5KEYS/7KEYS/9KEYS/24KEYSと既存短縮名を同じ値へ変換。順序・重複除去・未知制約省略を維持。trophy・公開規則は変えない。 | 実gzip→保存course→再出力で4種類、短縮名、他制約・重複を確認。既存exporterVersionで必要再生成へ接続。3・7後の変換境界を使う。 |
| 23 | 内蔵推薦表の保存済みprefix。生成内容が変わるリロードで属性だけを戻し、実folder・明示順は無修飾のまま保存する。 | 未修飾の生成hashを保ち、保存済みprefixを完成folder・順序へ一度投影。共通変換を使い、setter全域副作用・fingerprint拡張・推薦専用MEMO引継ぎは作らない。 | 6後。`PlaylistRecommendedTableOwnerTests` で異なる既存A/Bスコア→設定切替→生成hash差のあるリロード→実所属・順序・DB・必要出力。同一入力時の不要保存抑制。 |
| 24 | LR2名前なし所属のsort範囲。抽出は空文字を所属指定とし、sortは空白判定でALLへ広げる。 | ALL=null、名前なし=空文字をsortでも揃える。受理済み空白名を改名・拒否しない。ALLの代表値・スキーマ・索引・ランダム規則は変えない。 | `BmsPlaylistCustomFolderOutputTests` で同hashの名前なし/別所属へ異なるlevel/adddate。実出力#COMMANDをSQLiteへ適用しLEVEL/ADDDATE両方向と別所属除外。設定保存とは独立したSQL出力の修正。 |
| 25 | 順位cache採用後の成績派生値。`BmsLibraryIrService` の高得点更新と `BMSScore.Overwrite` がrank/rateを更新せず、DJ LEVEL表示・検索・内訳が古い。 | 採用境界で得点/notesから既存 `ScoreValueCalculator` に揃え、新規・既存行を統一。低得点・同点、clear/BP/combo、順位・未送信判定を保つ。RATE列の不具合とは扱わず、全score走査・追加通信・本人IR存廃へ広げない。 | 6後。`BmsLibraryIrServiceTests` と起動系列で高得点既存/新規/低得点/同点、1000notes・1800点→AAA/90%を独立値で判定。snapshotから検索・集計へ代表接続。 |
| 26 | beatoraja MAXの履歴内訳漏れ。clear合計へ加わるが `PlayHistoryPeriodSummary` とFCカード条件にMAXがない。 | 利用者合意どおり既存FC/PERFECT内訳へMAXを含め、カード件数と対象集合を揃える。専用カードを作らずPF/MAX個別検索、更新判定、元DB、LR2変換を維持。 | `PlayHistoryReadModelTests` の実scorelog→集計→カード検索で9→10、8→10、MAXでscoreのみ更新、既存FC/PFを確認。旧FC/PF文字列期待の維持を目的にしない。 |
| 27 | 一時停止中の先頭戻し。LR2 Restartはprocessを開始する一方、owner表示はPAUSEで次の操作も逆転する。 | 利用者合意どおりPAUSEを維持し、次の再生で先頭開始。LR2 paused Restartはprocessを開始しない。再生中Restart、現在曲・実player・callbackを保ち、大きな状態機械を追加しない。 | 実LR2 adapter＋process代替でStart→Pause→Restart→Resume→Pauseの表示と開始/終了を対で判定。BMIIDXはPauseがキー送信なので同契約への整合を別確認。次回方式予約・停止中も保持。 |
| 28 | 一時試聴の入力元名衝突。入力元の既存兄弟 `chart_.bms` を見ずに改名し、初回試聴が失敗する。非上書きなので原データ破壊とは分類しない。 | 入力元と同相対先の双方で候補を確認して一回だけ非上書き移動。候補ごとの改名を削除。既存 `_` 命名・相対配置・元名復元・素材借用と主/cleanup失敗を保つ。 | `PlaybackPanelViewModelTests` で通常発見した複数譜面directory package、入力元だけ/両側複数/ディレクトリ占有。元・既存先bytes保全、開始後回収と再試聴。 |
| 29 | 一時試聴の再開・再起動。初回回収後のLR2 Resume/Restart・BMIIDX Restartが回収済みパスを直接使う。 | 27・28後、初回と再読込みを同じ一時配置所有処理へ統合。現在曲・実playerの必要文脈だけを保持し、選択や次回factoryから再解決しない。副作用前に必要Lを非待機取得、Busyは状態/process不変。開始・cleanup Taskを既存寿命へ接続。曲全体で一時物・Lを保持しない。 | 実保留→導入先編集→adapter＋fake processで各起動時のファイル存在/内容と再回収。別選択、次回方式予約、Busy、停止/終了も確認。命令列だけのfake playerで代用しない。 |
| 30 | 前曲探索の方向。`Previous` が減算しても `FindPlayableChart` の除外・開始失敗後はNextへ進む。 | 実前曲探索は除外・InvalidDataException後も後方向。Next・先読み、単クリックRestart、repeat、候補なし・明示行開始の既存契約を保ち、新しい折返しを追加しない。 | 外部playerでA/BMSON/CのCから前曲、前方候補の開始失敗、端・候補なし。27〜29と再生ownerの書込みを直列化。 |
| 31 | 右クリック後の現在セル。行Bへ選択を変えても `CustomTableView.currentCellHit` がAに残る。 | 入力時点で行とセルを揃え、編集時だけ補正しない。集合保持・Shift基点・同じ行の別列を維持。Ctrl解除では既存SelectionModelの現在行へセルを揃え、未選択なら解除。 | 左A→右B→F2/文字/単一セルコピー、複数選択内・同じ行別列・Ctrl解除。F01の識別追従へ広げない。 |
| 32 | 範囲選択の主行通知漏れ。全選択後のShift操作で集合不変だと主行変更を通知せず、停止時ヘッダーが古い。 | 共通選択モデルの集合または主行の変化へ統一。player側補正や追加通知を作らず、同集合・同主行は無通知、基点・再生曲優先を維持。 | `CustomTableSelectionModelTests` と実接続でCtrl+A→Shift末尾/Down、主行変化一回通知、停止中表示更新、再生中現曲維持。31とは別原因として判定。 |
| 33 | プロパティPage URL空欄。WPFの空文字→nullがsetterで失敗し、表示だけ空・旧値残存となる。 | nullによる明示解除と非null検証を区別し、無効入力を空へ読み替えない。一般・フォルダ・出力設定の全面作り直しはしない。 | 実ダイアログ→保存→再開で解除と不正入力、header/dataを含む保存経路を確認。限定Binding probeを通常編集session・DB保存の保証にしない。 |
| 34 | 外部プログラム引数の二重引用符。`ExternalProgramArgumentTemplate` が引用符内のペアを反転だけで処理し、A"BをABにする。 | 現行Windows引用規則へtokenizerを揃える。空引数・placeholder後置換・単一引用符・backslash偶奇・不均衡拒否を維持。shell/生Argumentsへ切り替えない。 | `ExternalProgramArgumentTemplateTests` で `--label "A""B" "{filePath}"`、連続ペア、空、偶奇を独立token列で判定し、既存workflowの起動要求へ一例接続。 |
| 35 | Updaterドライブルート正規化。`Program.cs` が絶対化前に末尾slashを落としてD:とし、作業場所基準で解決してready前に失敗する。 | 絶対化後にrootを保つ末尾整形、包含判定・URI基点を整合。書込み可能な既存root配置を拒否せず、ready/proceed・管理領域・長パス・UNC・reparse拒否・rollbackを維持。種類変更修正は任意。 | `UpdaterPackageSyncTests`、`UpdaterProcessGatewayTests` の実プロセス境界で隔離した仮想ドライブ等のroot配置→ready→適用/rollback。通常実ドライブ直下へ試験物を置かず、正規化単体だけで完了にしない。 |
| 36 | 録音形式の偽表示。`EncoderIndex` getterが実行ファイル欠損時に保存値を変えずWAVEを返す。 | 常に保存済み形式を表示し、明示WAVE選択は通常保存。利用不能な別形式の警告・保存拒否、実変換時の既存代替規則は維持。 | 保存済みMP3・encoder欠損でもMP3表示。明示WAVEの保存/再表示/変換snapshot一致、別の利用不能形式拒否後も保存・表示一致。実設定ページの接続を確認。 |
| 37 | 履歴プリセット確定名。ストアはTrimするがeditorへ未正規化名を戻し、次の編集・削除の照合が外れる。 | 確定する一件を正規化し、保存とeditorで同じ内容を使う。新ID・保存形式は不要。 | ` 練習 ` 保存後に再表示なしで対象変更・改名・削除、JSONと表示が同じ一件。取消・空名・重複名を維持。 |
| 38 | 追加出力先の改名入力。setterの毎回TrimがPropertyChanged Bindingへ戻り、途中の空白を消す。 | 入力文字列は保持し、正規化は確定時だけ。保存経路整理とは独立したBinding修正。 | 実Bindingで `My`→`My `→`My Charts`、前後空白の確定時除去、空白名拒否、改名後の出力先・保存値・表参照一致。 |

## 責務別の参照と依存

| 領域 | 正本・共有境界 |
| --- | --- |
| 設定・保存・セル | [設定](../spec/runtime/settings.md)、[設定の保存](../spec/runtime/settings-persistence.md)、[一覧](../spec/ui/table-view.md)、[保存と出力](../spec/playlist/storage-and-export.md)。設定ViewModel/Windowへの書込みは直列。LEVELはセル終端後、score・BMTの失敗契約はそれぞれ独立。 |
| 物理変更・資源・推定 | [ライブラリ変更](../spec/library/mutations.md)、[整合性](../spec/library/file-db-consistency.md)、[重複](../spec/library/duplicate-files.md)、[導入](../spec/library/drop-install.md)、[推定](../spec/library/install-estimation.md)、[性能](../spec/core/performance-and-scale.md)。ルート移動・削除と祖先索引、native2件は共有パスを直列化。 |
| 表・URL・履歴 | [取得](../spec/playlist/downloads.md)、[Table URL取込み](../spec/playlist/table-url-import.md)、[BMT](../spec/playlist/bmt-export.md)、[LR2出力](../spec/playlist/lr2-custom-folders.md)、[履歴](../spec/playlist/play-history.md)。URL比較の共有とcache・取得所有者の結合は別判断。BMT混在名、推薦MEMO、本人IR存廃は任意。 |
| 再生・画面・外部起動 | [再生パネル](../spec/ui/playback-panel.md)、[一覧](../spec/ui/table-view.md)、[外部起動](../spec/ui/external-launch.md)。PAUSE中の起動要否、候補名決定、再開始の順で共通ownerへ接続。右クリックと範囲選択の原因を区別。 |
| 更新 | [ポータブル更新](../spec/integration/portable-update.md)。root正規化だけを必須対象にし、管理file→directory遷移の実装は混ぜない。 |

Everything SDK 3.0.0.9の取得契約は、容量0の返却がNUL込み必要容量、非ゼロ容量では書込み長である。補助平面文字の直前では容量を一単位残して終わり得るため、`capacity - 1` 一致だけを切詰め条件にしない。調査で参照したLinux上のSDK getter限定実験と抽出索引コードの計測は、Windows IPC・製品bridge・DB/UIの再現や完了時間の非退行を保証しない。実装時は通常短パスの呼出し数と製品の同条件の完了時間、正規キー増加の影響を分けて確認する。恣意的な許容率や無関係な全体ベンチマークを追加しない。

共有表コンテキストメニューのPL04は、PlacementTargetとMenuItem.DataContextの参照元が異なるという指摘だけでは別表操作を実証できず、必須改修に数えない。限定WPF probeのA→B→Aでは一致した。確認する場合は実MainWindowで複数表を開閉し、現在対象と処理先を判定する。明示DataContext代入だけのテストで保証せず、乖離を再現した場合だけ対象解決の修正を再判断する。新cacheやメニュー全体の書換えを先行しない。

## 実装開始・検証・完了条件

各単位の着手前に、[テスト設計](../spec/development/test-authoring.md)で不足する恒久保証だけを独立設計し、ルートの承認と[引継ぎ点検](../spec/development/agent-workflow.md#引継ぎの点検)を経て着手する。既存の保存段階省略、旧要求固定、旧スコア保持、候補完了引継ぎを正解の根拠にしない。置換・削除するテストは保証の移管先または不要な理由を明確にする。

判定条件は既存サービス・実SQLite・実FS・native等へ置き、WPFは実Binding・イベント接続の代表へ分担する。新規テスト不要の判断と既存回帰の実行範囲を区別する。最新入力での必要媒体再保存、入力修正後の成功、未知設定・管理登録・無関係候補の保持、部分保存の失敗通知、通常閉鎖・再表示・終了joinも既存保証として残す。

各単位の反復は `scripts/verify-refactor.ps1` の対象を絞った `Quick`、通常開発のコミット・統合前はルートが `Functional`、公開候補は `Full` を選ぶ。[検証仕様](../spec/development/testing.md)の環境・実行回数・失敗分類に従う。native変更は生成物と配布整合も確認する。今回の計画統合自体はMarkdownの構文・参照・UTF-8・LF・差分確認だけを対象とし、製品検証成功は主張しない。

保存・削除・互換性、受付・Task寿命、索引・走査完全性、主体の責務を実質変更する単位は重大変更として扱う。各最終差分で分類し、凍結した新規コンテキストの予備レビュー後、独立した通常または重大最終レビューへ進む。重要度ラベルだけでレビュー種別を選ばない。

通常入口の必要な再現と修正後の結果、維持する取消・失敗・保存・表示・性能を確認し、必須検証と最終レビューを通過して完了とする。必要な契約・実装・テスト対応を機能仕様へ移し、完了項目は本書から削除する。全項目が完了したら本書と不要な参照を削除し、[計画一覧](README.md)を更新する。
