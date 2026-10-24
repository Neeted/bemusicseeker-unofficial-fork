# リリース追加レビューの必須修正

## 目的と採用状態

`.tmp/bemusicseeker-release-review-plans` の10文書を現行の通常入口と仕様へ照合した結果を、未完了の判断・実装候補として残す。文書内の修正案や優先度は利用者指示とは扱わない。既存の[必須修正](release-blocking-fixes.md)は継続し、合意済みの[任意改善](release-follow-up-candidates.md)は今回実装しない。

照合版は `50476c31`。同版の本番コードは元資料の基準 `a84a3e74` と同じである。その後の `4e0477ec` はプレイリストのフォルダ構成公開・選択を変更するため、当該周辺の着手時には新しい版を使う。以下の因果は静的確認を中心とし、全項目を実再現済みとは扱わない。

利用者は、以下の追加不具合を既存残作業に続けて今回実装することと、重複整理後の自動選択も手動選択と同じフォルダ識別へ揃えることを承認した。以下の9単位を今回の必須修正として扱う。devブランチで必要な検証・レビュー後にコミットする既存の進め方を維持する。

## 妥当性と修正境界

| 対象 | 確認結果と根拠 | 採用する場合の境界 |
| --- | --- | --- |
| 重複グループの手動選択・消失 | `DuplicateGroup.Header` は先頭Titleで一意ではない。`MainWindowViewModel.NormalizeDuplicateViewParameter` が見出しへ落とし、`RegularChartListOwner.TryResolveDuplicateSource` は最初の一致、未一致は全グループへ広げる。選択したグループの一覧と、明示した重複ルートの全件表示という[既存契約](../spec/library/duplicate-files.md)に反する。物理削除先まで誤ることは未確認。 | 現在の検索結果内で一意な所属フォルダによる短命な識別を第一候補とし、グループ指定の未解決は空とする。永続ID・全ライブラリ索引は増やさない。整理後の次／存続グループの自動選択も同じ識別に統一し、見出し照合の旧経路を残さない。 |
| アプリ内フォルダ変更後の祖先資源索引 | 親の譜面ディレクトリは子資源を相対キーで集約する。`LibraryResourceIndexOwner.UpdateMovedFolderReferences` は移動範囲のキーだけを移し、範囲外の祖先を更新しない。明示再スキャンも古い索引Entryを使い物理確認を省くため、消えた旧資源を存在扱いにする。既存の資源のみ導入後の保守値共用、外部変更の操作案内とは独立。 | 成功した物理変更に対し、影響する旧新祖先の資源索引を最終配置へ揃える。削除・統合にも同型の不足があるため入口を確認する。即時に関連全譜面を再評価する機能は含めない。自己索引・逆引き・世代の整合を一変更セッションで保ち、全件走査を常用しない。 |
| UTF-8 BOM付きBMSの表示メタデータ | `BmsEncodingDetector.DecodeBytes` はBOMを自動検出するため、CP932候補でUTF-8復号に成功してCP932名を返す。`BmsChartFileParser` はその名前を根拠に表示補正を省き、先頭TITLE欠落・日本語文字化けになる。新規登録・明示保守・LR2同期・パッケージ読取りへ到達する。 | 判定した方式と復号本文を一致させる。今回の対象はUTF-8 BOMと表示メタデータ。資源名のCP932解釈、ハッシュ、BOMなし判定、プロパティの既存任意改善へ広げない。UTF-16/32等の対応拡張やパーサー版更新は自動採用しない。 |
| 転送されたアーカイブの保存名 | `PlaylistUrlAcquisitionWorkflow.ResolveDownloadedArchiveFileName` は元URLにqueryがある場合だけ最終応答URIを使う。`/download/123` → `/song.zip` の正常転送が非対応名としてブラウザーへ回る。[取得仕様](../spec/playlist/downloads.md)は転送後URIの保存名解決を含む。 | Content-Disposition優先を保ち、query有無に依存せず有効な最終名を使う。最終名が使えず元要求名だけが有効な既存ケースを保つ。拡張子推測・追加通信・HTML再帰探索は増やさない。 |
| JSON出力の相対data参照 | header/dataの別ディレクトリ選択は受理契約だが、`PlaylistExport` は保存先をbasenameへ落とす。Data_url未設定時に出力headerが実dataとは別の場所を参照する。[保存・出力仕様](../spec/playlist/storage-and-export.md)に反する。 | 出力時だけ、headerから実dataへの正しいURIを作る。既存の非空Data_urlの意味と正本を保つ。予約文字・異ドライブを含む参照を検証する。別ディレクトリを新たに拒否しない。異ドライブのfile URIの参照成立を、公開時の可搬性と混同しない。 |
| 外部表の転送後の相対参照 | `PlaylistExternalSyncOwner.GetExternalContentAsync` は最終応答URIを捨て、相対header/dataを元URLへ解決する。末尾slashを補う転送やheader転送で取得先を誤る。相対entry URL・公開するdata基準への影響もある。 | 今回取得した文書の実URIを相対解決へ用い、利用者の同期元を一時転送先で置換しない。返却表・保存後・entry URLにも同じ意味を保つ。表ごとの取得回数・制限・取消・解放と、失敗時の既存表保持を維持する。 |
| 前曲で非対応譜面を飛ばす方向 | `Previous` は添字を減らすが、`FindPlayableChart` は除外時に常に増やす。外部プレイヤーでA/BMSON/CのCから前曲を要求するとCへ戻る。InvalidDataException後の再探索もNext固定。 | 実際の前曲探索は除外・開始失敗後も後方向を保つ。Next・先読みは前方向、単クリックRestart・repeat・候補なし・明示行開始の既存契約を保つ。先頭での新しい折返し等は追加しない。 |
| 右クリック後の行と現在セル | `CustomTableView` の左クリックはcurrentCellHitを更新するが右クリックは行選択だけを変える。Aを左クリック後Bを右クリックすると行B・セルAとなり、F2/文字入力/単一セルコピーは表示どおりAを使う。更新や非同期競合は不要。 | 入力時点で行と現在セルを整合させる。右クリックの集合保持・Shift基点・同じ行の別列を保ち、編集時だけBへ強制しない。Ctrl解除の現在行の決め方は既存SelectionModelを変えず、その行へセルを揃え、未選択ならセルを解除する。任意のプレイリスト識別追従へ広げない。 |
| プロパティのPage URL空欄 | WPF既定のTextBox→Uri変換は空文字をnullでsetterへ渡す。既存Page_url setterはnullを逆参照する。限定probeでは本番setterに空文字をBindingすると表示は空、値は旧URLのまま残った。header/dataも含む保存・再開の完全経路はまだ未検証。 | nullによる明示解除と非null検証を区別し、無効入力を空へ読み替えない。実ダイアログ→保存→再開で再現・修正を確認する。一般・フォルダ・出力設定については既存保証を活用し、別の不具合がなければ全面的なUIテスト行列や作り直しを追加しない。 |

参照した主な実装は [重複一覧](../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[資源索引](../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/LibraryResourceIndexOwner.cs)、[文字コード判定](../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/BmsEncodingDetector.cs)、[URL取得](../../BeMusicSeeker/Models/Playlist/PlaylistUrlAcquisitionWorkflow.cs)、[JSON出力](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistExport.cs)、[外部表取得](../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs)、[再生パネル](../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[共通表](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs)、[プロパティ編集](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs)。着手時に実際の配置と最新コードを再確認する。

## 確認対象に留める指摘

共有表コンテキストメニューは有効化がPlacementTarget、処理がMenuItem.DataContextを使う。しかし同じ共有メニューをA→B→Aで開くWPFの限定probeでは、双方のDataContextがその都度一致した。参照元の違いだけではPL04の別表操作を断定できず、現時点で本体修正を採用しない。

実MainWindowのメニューを複数表で開閉し、現在の対象と処理先が一致する代表検証は候補とする。既存テストの明示DataContext代入ではこの接続を保証しない。乖離を再現した場合のみ、現在のPlacementTargetからの対象解決へ統一する。新しい対象cacheやメニュー全体の書換えを先行させない。

Page URLのprobeは、既定TwoWay Bindingと本番setterの入力境界だけを動かした。ViewModelは反射で用意したため通常の編集session構築、DB保存、再開を保証しない。共有メニューprobeもMainWindow全体の接続確認ではない。これらを実アプリ操作済み・全画面検証済みとは扱わない。

## 採用する重複選択契約

手動選択と整理後の自動選択を同じフォルダ識別へ統一する。二フォルダ統合・同一フォルダ整理は操作前に捕捉した次のグループ、三フォルダ以上の統合は存続するグループを選ぶ。同名・空名でも取り違えず、捕捉した対象が消失した場合は別グループや全件へ読み替えない。明示した重複ルートだけが全件を表示する。フォルダ単位の選択との区別は保つ。

恒久ID、選択状態の永続化、追加の再検索・全行実体化を導入しない。現行仕様が認めていた見出し最初の一致という補助動作は、実装と同じ単位でこの契約へ更新する。

## 着手順とコミット単位

既存の設定実操作所有・閉鎖寿命・録音表示・プリセット・改名入力を先に完了する。続いて以下を独立単位として進める。

1. 重複グループの選択識別と消失時の空結果。次／存続グループの自動選択も同じ単位に含める。
2. アプリ内の物理変更と祖先資源索引の整合。影響する操作を一つの索引責務へ集約する。
3. UTF-8 BOMの自動判定と表示メタデータ。
4. 転送アーカイブの保存名。
5. 転送した外部表の相対参照。4とHTTP基盤を共有しても、本文・保存名・参照元の責務は別単位とする。
6. JSON出力のdata参照。
7. 前曲送りの探索方向。
8. 右クリック時の現在セル。
9. プロパティURL解除と必要な実編集・保存代表。PL04を確認する場合は対象解決の代表検証を別責務として分ける。

各単位で合意した契約から独立テスト設計・引継ぎ点検、実装・局所検証、Functional、凍結・新規全体予備・独立最終レビューを行う。永続化・索引・主体の責務・Task寿命を実質的に変える場合は重大レビューとする。単なるUI操作列数を目標にせず、条件の判定は既存サービス、実Binding・イベント接続は代表WPFへ分担する。

実装時に入力拒否の拡大や意味の変わる参照・選択規則が必要になれば、契約を確定してから進める。全件走査、永続状態、予約、自動再試行、巻戻しを仮説だけで追加しない。

## 完了条件

採用した不具合について通常入口の再現と修正後の結果を必要な境界で確認し、既存の取消・失敗・保存・表示・性能契約を保つ。必要な契約とテスト対応は機能仕様へ移し、完了した項目は本書から除く。全完了時は本書と計画一覧の参照を削除する。任意改善は別資料の採否条件で扱う。
