# プレイリストのLR2カスタムフォルダ出力

## 目的と適用範囲

プレイリストから `.lr2folder` と対応するLR2のフォルダ行を生成する契約を定めます。正本のDB保存とは別の処理で、LR2連携モードだけが実ファイルを出力します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

設定からの保存・生成と未登録追加先名の扱いは本書と[設定](../runtime/settings.md)を正本とします。

### 出力先と事前検査

出力配置は起動時の保存値から初期化する小不変入力を使います。user.config・必要なLR2 XMLと表header DBの保存が完了するまで新配置を公開せず、未保存draftは通常出力・交差判定・全体同期の署名/currentnessへ反映しません。古いプロパティ画面から保存するときも、局所受付後に現行配置と正本対象へ固定します。保存失敗・部分保存・公開後失敗は[設定仕様](../runtime/settings.md)に従います。

設定画面の通常・ROOTの選択、追加先の追加・削除・改名は一操作だけを確定し、他区分は最新の確定配置を使います。競合するBMS・LR2詳細候補は破棄を確認し、実受付と最終検証に成功した採用点で破棄します。picker取消・初期同値通知・検証失敗・受付拒否では元候補を保持します。利用者の明示再選択は最新入力の再保存・再生成に使えます。出力公開後はBMS候補を確定一覧へ同期し、追加・削除差分の引継ぎは行いません。追加操作の旧名から新名への対応はその操作の終端で破棄し、保存失敗後の追加候補も公開済み配置から作り直します。明示再操作は最新入力を再保存・再生成し、過去要求・成功段階を保持しません。確認と処理寿命は[設定](../runtime/settings.md#競合する候補の切替)に従います。

既定の通常出力先は `LR2CustomFolderOutputBaseDir`、追加先は `LR2CustomFolderAdditionalOutputBaseDirs`、ルート型は `LR2CustomFolderOutputBaseDirRootType` です。追加先はJSON文字列配列でフルパスを保存し、表示とDBの選択値には末尾のディレクトリ名を使います。

追加先の改名欄は生入力を保持し、入力中には保存・公開しません。ページ移動や無関係な通常項目の保存では候補を保ち、通常閉鎖では最新公開名へ戻します。同じViewModelの再表示でも破棄した名前を持ち越さず、閉鎖・再表示だけでは保存しません。改名確定時だけ前後空白を除去し、内部空白を残した末尾名を追加先のパス・保存値・参照する表の名前に揃えます。正常確定済みの選択先と正規化後のパス・名前が同値になる明示改名は、自己候補の表示だけ選択先名へ揃えます。確認・保存・生成を追加せず、他候補も破棄・採用しません。空白だけの名前と `.` は既存の確定検査で拒否します。実Bindingの入力・破棄と処理寿命は[設定の改名入力](../runtime/settings.md#保存の受付と処理選択)と分担します。

表の `custom_folder_output_base_name` がNULLなら既定先、登録済み名なら追加先を選びます。通常型で使う保存名が設定にない場合は、通常先へ読み替えず該当表の出力を止めて知らせます。既存データも対象とし、元の保存名を保持・表示し、表の保存先再選択で解消します。未登録名からの再選択では旧先を推測せず、新先だけを生成し、通常先にある無関係なファイル・管理行を保ちます。設定画面で追加先を削除した場合は参照をNULLへ、改名した場合は新名へ更新します。ルート型の間も通常先の選択値は保持し、実効出力先だけルート型を優先します。追加先名が未登録でもROOT出力は続け、通常型へ戻した時にその名前を使う場合は出力を止めます。

`output_dir` は入力をtrimしてShift_JIS互換にしたフォルダ名です。空または同じ規則で変換した表名と同じならDBではNULLとし、表名由来の実効値を使います。通常型は選択先の下、ルート型はルート型基点の下へこの名前のディレクトリを作ります。

新規draftは登録前の旧配置を持たず、初回保存では選択した通常・追加・ROOT先へ既存の出力管理主体で生成します。旧先整理は既存配置からの移行だけに適用し、共通基点や兄弟表のファイル・LR2行を保持します。ROOTの実効先はXMLへ保存し、失敗をメモリ内登録だけで成功扱いにしません。初回出力の失敗後も登録済みIDの未完了出力を同じ捕捉設定で明示継続します。空表の生成対象と空ディレクトリ整理は通常の出力規則に従います。

起動・差分更新・初期化の前には全出力基点を検査し、読取りと自己所有の一時ファイルの作成・書込み・削除を要求します。使えない基点を自動作成して成功扱いにしません。配下の表ディレクトリや出力ファイルがまだないことは失敗ではありません。スタンドアロンではLR2出力先の残存設定で起動を止めません。

設定変更前に有効な導入先を今回の通常・追加・ROOT管理領域へ取り込む変更は、同一・包含親のどちらも保存・XML・実配置変更前に拒否します。導入先以外の既存登録の採用は維持し、先に別の確定BMSルートへ導入先を変更すれば再選択できます。未受理候補は確定値へ戻し、再操作は今回入力と他項目の確定値から処理します。初回・既存不正状態の段階保存と比較方法の正本は[設定の導入先保護](../runtime/settings.md#カスタムフォルダと検索対象)です。

基点同士の同一・親子配置は拒否します。通常・追加先は既存BMS登録と同一か、重ならない場所だけを許可し、ルート型先は既存登録の親も許可します。既存登録を管理領域として採用する場合の保存時確認、旧管理領域の保護、検索対象の区別は[設定](../runtime/settings.md#カスタムフォルダと検索対象)を正本とします。

管理表同士の実効絶対パスは、同一・親子のどちらも許可しません。大文字小文字、区切り、`.`・`..` を正規化して、名前・出力名・基点選択・ルート型・基点設定の変更後集合を副作用前に検査します。一括変更は対象同士と未選択表を含めます。外部表の新規登録も既存表と全候補の変更後集合を検査し、一括登録に衝突があれば全件を登録前に拒否します。`Parent` と `Parent2`、兄弟、重ならない別基点は許可します。スタンドアロンにはこのLR2配置検査を適用しません。

保存済み値が包含する場合も出力・移行・出力削除・表削除・検索ルート同期を拒否し、DB・正本・ファイル・検索ルートを変更しません。自動改名・設定修復・包含を保護しながらの出力は行いません。外部情報初期化の対象別除外は[プロパティ編集](../ui/playlist-properties.md#サマリーからの一括編集)に従います。

### jukeboxとの同期

通常・追加先は利用者設定を正本として `jukebox` に不足分を補い、同一登録は保持します。両者の同期・起動時修復は共通の配置検証を使い、全候補を検証してからディレクトリ作成・登録追加へ進みます。既存登録の親・子を指定した場合は設定不備として拒否し、親登録を外して子へ置換したり、不正な出力先だけ黙って省略したりしません。

ルート型は基点そのものではなく、各表の実効出力ディレクトリをルートとして扱います。既存登録と同じ基点、または既存の表ルートを含む親を指定でき、子ディレクトリが未生成なら必要に応じて作成します。保存時と起動時は基点・不要な旧子登録を現在の表ディレクトリへ揃えますが、基点の外側にある親登録は削除対象にせず、基点がその子となる設定を拒否します。起動時の完全な整理は表一覧を読み込んだ後に行います。`ReloadTables` は不足する表ルートを補い、不要ルートの完全な整理は保存時・起動時が担当します。

### 出力除外と種類

`ignore_folder_output` は出力しない種類を表すビットです。既存値に新しい種類のビットがなければ、その種類は出力します。旧版の全除外相当を現在の全除外値へ拡張しません。`entry_type=Folder` では保存値に関係なくレベル別出力を無効にします。

新しい通常表は `PlaylistDefaultIgnoreFolderOutput`、推定表とおすすめ表は専用の固定値を使います。既存表の外部同期では保存済みの値を保持します。

| 種別 | bit | `PlaylistDefaultIgnoreFolderOutput` の既定 | 推定表 プレイリスト の既定 | おすすめ表 プレイリスト の既定 |
| --- | ---: | --- | --- | --- |
| 利用者フォルダ | `0x001` | 出力 | 出力 | 出力 |
| レベル | `0x002` | 出力しない | 出力 | 出力しない |
| 頭文字 | `0x004` | 出力しない | 出力しない | 出力しない |
| クリア | `0x008` | 出力 | 出力しない | 出力しない |
| DJレベル | `0x010` | 出力 | 出力しない | 出力しない |
| 譜面特性 | `0x020` | 出力しない | 出力しない | 出力しない |
| その他 | `0x040` | 出力しない | 出力しない | 出力しない |
| ランダム | `0x080` | 出力 | 出力しない | 出力しない |
| BPM順 | `0x100` | 出力 | 出力しない | 出力しない |
| BP順 | `0x200` | 出力 | 出力しない | 出力しない |
| プレイ回数順 | `0x400` | 出力 | 出力しない | 出力しない |
| 最終プレイ順 | `0x800` | 出力 | 出力しない | 出力しない |
| 全曲 | `0x1000` | 出力 | 出力 | 出力 |

通常の既定除外は `Level | Alphabet | CategoryAll | Other` です。`AllFolders=0x1FFF` は全出力を禁止する値です。

全曲と利用者フォルダが対象範囲を選び、クリア、DJレベル、BPM順、BP順、プレイ回数順、最終プレイ順はその範囲ごとに出力します。レベル、頭文字、譜面特性、その他は独立した全体分類です。

| 実装上の種類 | 生成内容・条件 |
| --- | --- |
| `AllSongsFolder` / `UserFolder` | 全体の「表名 ALL」と各フォルダ。削除済み譜面を除き、空のフォルダ名は表名を表示する。 |
| `LevelFolder` | レベルを切り捨てた整数ごとの `LEVEL n` とNULL用の `LEVEL ???`。 |
| `AlphabetFolder` | `A.B.C.D.` から `U.V.W.X.Y.Z.` までの6区間と `OTHERS`。 |
| `ClearFolder` | `CLEAR FOLDER` 配下に番号付きのNO PLAY、FAILED、ASSIST、EASY、CLEAR、HARD、FC、P.A。タイトルには番号を付けない。LR2のclear=2をEASYビットでASSIST/EASYに分け、FCはP.Aビットを持たないものだけとする。 |
| `DJLevelFolder` | `DJ LEVEL` 配下のAAA、AA、A、UNDER A。A未満とスコアなしを最後の区分へまとめる。 |
| `CategoryAllFolder` | `ALL LONG NOTES` と判定種類別。 |
| `OtherFolder` | `MY BEST`、`NEW SONGS`、`REMOVED SONGS`。IR取得・未送信検知が有効なときは `UNSENT SONGS`。 |
| `RandomFolder` | 対象範囲・レベル・クリア・DJレベルの各出力にランダム版を追加する。`ORDER BY random()` と `#MAXTRACKS 1` の両方が必要。 |
| `BpmSortFolder` | `chart_info.mainbpm` の昇順、欠損は末尾。パーサー版を条件にしない。 |
| `BpSortFolder` | `score.minbp` の昇順、未プレイ・欠損は末尾。 |
| `PlayCountSortFolder` | `score.playcount` の降順、未プレイ・欠損は末尾。 |
| `LastPlaySortFolder` | `bms_lr2_last_play.last_play_at` の降順、欠損は末尾。他の日時へ代用しない。 |

最終プレイ順は履歴スキーマ未導入でもファイルを生成しますが、LR2側で開くには対象表が必要です。SQLで現在の履歴を参照するため、プレイごとの再出力は不要です。

### ファイルとSQL

管理 `.lr2folder` はDBを正本とする再生成可能な出力です。大量生成と差分出力の速度を維持するため直接書込みを使い、書込み失敗時の旧本文保全は保証しません。全ファイルの原子的保存や全本文の再検証は行いません。利用者が保存するJSONの保証は[JSON出力](storage-and-export.md#json出力sqlバックアップと復元)に従います。

各相対ディレクトリで `0000.lr2folder` から採番し、Shift_JISで出力します。通常版を先に、ランダム版をその後に並べます。本文は `#COMMAND`、`#MAXTRACKS`、`#CATEGORY`、`#TITLE`、`#INFORMATION_A`、`#INFORMATION_B` を持ちます。カテゴリは表名、タイトルは表示フォルダ名です。

コマンドはLR2/OpenLR2が `song LEFT JOIN score ON song.hash=score.hash` に適用できるWHERE句断片です。ファイル単位の表は所属MD5へ絞り、フォルダ単位の表は所属MD5から得た `song.folder` へ広げます。`playlist_entry`、`chart_info`、必要な `ir_score` と履歴表も参照します。

最終プレイ順は次の固定SQLを使います。

```sql
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

同じ生成内容から `folder` 行を同期します。`date` は実際の更新時刻、既存 `adddate` は保持し、新規だけ生成時刻を使います。通常先→表ディレクトリ→連番ファイルという階層を保ち、ルート型は各表ディレクトリだけをLR2ルート直下へ置きます。階層を平坦化しません。CRC32や表の列は[楽曲DB同期](../integration/lr2-song-db.md)を参照します。

### 管理領域の整理

表の出力ディレクトリ内は管理領域です。同じ先への再出力では期待集合にない `.lr2folder` を削除します。出力先変更、ルート切替、表削除では旧表ディレクトリ全体と対応範囲のDB行を整理します。基点直下の他の項目は変更しません。別表の包含に備えた子孫保護は持ちません。同一表の旧親から新子への移行、旧パスの再利用、管理基点外の保全は移行整理の別の保証として維持します。通常基点と追加基点を同じ設定要求で変更するときは、承認した現在名から最終登録名への対応を移行準備へ明示して渡し、未改名の表を含む配置検査・候補出力・整理時の保全先を同じ最終配置で解決します。

ROOT基点がドライブルート・UNC共有ルートの場合も、整理対象は基点直下の当該表ディレクトリです。基点全体を削除対象へ広げません。[一般BMS一覧・導入先からの除外](../runtime/settings.md#カスタムフォルダと検索対象)は基点全体と子孫を対象とする別の契約です。

### 修復と一括反映

設定変更後の生成失敗で設定を旧値へ戻しません。現在の設定と保存済み表headerから生成し直し、旧先残存の整理だけを理由に後続設定を制限しません。設定の再操作に過去の移行段階は持ち越しません。追加先の改名・削除は不変なheader更新をDBへ保存してから表メモリへ公開し、DB保存失敗を生成物の残存警告へ格下げしません。

起動時は通常・追加の不足検索登録を補い、表header読取り後にROOTの表別登録を補います。LR2モードの出力対象について、現在配置の署名・物理不足・管理行等の既存判定に従って再生成します。過去の改名意図やDBに保存できなかった削除を再現する機構ではありません。

出力状態がない、入力が変わった、物理ファイルが欠落した、ファイル・出力ディレクトリ・祖先の更新時刻が変わった場合は再確認対象にします。その場合だけ正本から軽量な期待集合を作り、ファイル行・ディレクトリ行の欠落、日時・親の不一致、余分な行を調べます。列挙基盤が使えない場合は再作成対象にし、全ファイルの個別存在確認を通常経路にしません。

状態が現行ならDB行だけの外部編集を常時監査しません。ただしjukeboxへルート型表を補完した直後は、表ディレクトリ自身の行を完全一致パスで確認します。末尾区切り付きパス、`type=1`、`parent=e2977170` に限り、不一致ならその表を再確認対象へ戻します。

設定画面からの手動再同期は確定した環境を使い、対象を変える環境候補の破棄案内を既存確認へ統合します。取消・実L/P受付拒否では画面と候補を保持し、実受付成功後だけ既存の画面閉鎖を行います。閉鎖後も準備・保存・cleanupの実終端TaskをShellの終了待ちへ引き継ぎます。実受付の通知が失敗した場合も両受付を解放し、準備・保存へ進まず元失敗を伝播します。この境界は `BmsLibraryLr2SongDbSyncTests.QueueLr2SongDbSync_AdmissionNotificationFailureReleasesBothLeasesBeforePreparation` が確認します。

欠落表や手動再同期の全対象はまとめて生成し、物理ファイルの差分だけを書き、DB同期は一回で行います。表ごとに旧出力を消して同期を繰り返しません。生成開始、表の処理、物理反映、DB同期、状態保存まで進捗と診断を出します。

管理外の `.lr2folder` 探索は、管理表ディレクトリを列挙時の除外範囲として渡します。全件を候補化してから捨てません。保持した走査結果と現在の除外範囲が変わった場合は現在の範囲で探索し直し、出力前の古いファイル集合を出力後の同期に使いません。

日常部分出力・全体生成・設定移行の受付条件と寿命は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。進捗は既存の最新状態通知キューを使い、購読先の例外は診断し、物理ファイル・DB・保存状態の結果と区別します。

基点・表ディレクトリ移行は捕捉済み旧先・新先を用いて処理します。新しい先の確定後に旧先削除が失敗しても、新しい先のファイル・DB・出力状態、旧先の残余と元警告を保持します。次の通常要求は新しい先を使い、古い編集画面の配置から旧先cleanupを再開しません。プレイリストのプロパティ編集における同じ編集セッションの明示保存継続は、保存結果に捕捉した旧先・新先の絶対パスと旧型・旧基点を使って未完了部分だけを先に処理します。設定画面の局所出力確定では、今回捕捉した旧先の絶対パスと旧型・旧基点を同じ操作の終端までだけ使い、現在正本から生成します。必要な設定媒体とheader DBを保存してから公開し、生成失敗・旧先残存のどちらも過去要求の再開情報として保持しません。正本保存・必要生成の失敗と、旧先整理だけの警告を区別します。次の明示操作はその時点の最新入力を再保存して生成します。

設定操作で新先生成後の旧管理行整理に失敗した場合は、旧出力先と原因を残存警告として知らせ、必要な外部folder同期へ進みます。一般的な保存失敗だけに置き換えず、表プロパティ保存の未完了継続とは分けます。`OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest` は実DELETE故障による旧行残存、新先維持、通知内容、後続同期への接続を確認します。

起動の保存済み表の必要出力は、最小カタログと必要譜面情報・ハッシュの準備後、親L/P内で直接待ちます。外部ネットワーク同期は親受付解放・操作解禁後です。全体LR2が必要なら修復と共通の生成・検証経路から完全な面を一度だけ返します。開始・更新・全終端は空でない発生元と同じ親関係を持つ要求識別で通知し、BMTはschedulerの暗黙実行文脈を参照しません。カスタム修復のfinallyにも同じ要求を渡します。実出力の接続は[`StartupRequiredInitializationTests`](../../../BeMusicSeeker.Tests/Startup/StartupRequiredInitializationTests.cs)、BMTの生産側通知は[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)で確認します。

ROOT登録の補完・同期、表削除、プロパティ、一括編集は同じLR2設定所有者へ今回の登録差分を渡します。ディレクトリ準備・出力移行はXML保存排他外で行い、保存失敗は正本へ採用しません。公開では別セクションの部分保存を巻き込みません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| ドライブルート配下の実ROOT表削除と管理外楽曲の保持 | `PlaylistRemovalWorkflowOwner`、`PlaylistCustomFolderOutputMaintenanceOwner` | [`SettingDialogLr2OwnerTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogLr2OwnerTests.cs) の `Lr2DriveRootOutput_PreservesConfirmedInstallThroughGenerationInstallationAndTableRemoval` は設定で保存した管理外導入先への実導入後、同じ表の物理ディレクトリ・folder行・表登録・jukebox登録の削除と、楽曲の実体・DB行・表外ファイルの保持を確認します。別名は全owner終端後に解除し、清掃は所有した一時実パスだけへ行います。 |
| 同じLR2正本へのROOT登録保存と限定公開 | [`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)、[`Lr2ConfigOwner`](../../../BeMusicSeeker/Models/LR2/Lr2ConfigOwner.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)、[`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が各入口の保存・出力を、[`部分保存と実表削除`](../../../BeMusicSeeker.Tests/Settings/SettingDialogLr2OwnerTests.cs) が別操作の未公開値を巻き込まないことを確認します。 |
| 同一・包含の両方向拒否、名前表記と区切り境界 | `ManagedPlaylistOutputPlacement` | `ManagedPlaylistOutputPlacementTests.FinalDirectories_RejectOnlyIdenticalOrNestedPaths` は条件表を共通判定へ集約する。 |
| 保存済み不正配置の出力・削除・移行・root準備拒否 | `PlaylistCustomFolderOutputMaintenanceOwner` | `BmsPlaylistCustomFolderOutputTests.SavedNestedOutput_RejectsOutputAndRemovalBeforeMutation`、`BmsPlaylistMigrationAndRegistrationTests.MigrateCustomFolderOutputDirectory_RejectsSavedNestedLayout` は旧保存値から実経路を通し、DB・ファイルを保全する。 |
| 同一表の旧新、旧path再利用、管理基点外 | 移行の旧先整理 | `BmsPlaylistMigrationAndRegistrationTests.MigrateCustomFolderOutputDirectory_PreservesSameTableDestination` と既存の管理基点外・`Folder`/`Folder2`・今回旧新配置の整理ケースで維持する。 |
| 外部新規登録の配置拒否と受付解放 | `PlaylistAggregatePersistenceOwner.PrepareExternalRegistration` | `BmsPlaylistMigrationAndRegistrationTests.ExternalTableRegistration_RejectsOverlappingCandidateBeforeMutation` は単一と安全候補を先頭に含む一括でDB・正本・ファイル・XMLの未変更と次の安全登録を確認する。 |
| 通信・必要公開までの受付と次明示要求 | `PlaylistExternalSyncOwner`、`PlaylistWorkspaceViewModel` | [`BmsPlaylistExternalReloadTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistWorkspaceExternalSourceTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs): 実通信前から同側Busy、未受理の削除・登録が自動再実行されないこと、実DB/出力と解放後の新明示要求を確認する。 |
| 日常の非交差並行と正本・CP932・フォルダ実反映 | `ApplicationComposition` | [`DailyLibraryDiffAndIndependentPlaylistRegistration_CompleteActualChangesConcurrently`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionAdmissionTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 導入先を出力管理領域へ取り込む設定変更 | [設定の出力候補・局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、`ValidateOutputBasePreservesInstallDirectory` | `SettingDialogCustomFolderOutputBaseTests.OutputBase_ProtectsConfirmedInstallDirectory` が各基点・正規化比較を、`SettingDialogEditCompletionTests.OutputConfirmation_ProtectsInstallDirectoryUntilAnotherConfirmedRootIsSelected` が拒否の実媒体・配置無変更と導入先変更後の実移行を確認する。今回の最新入力による再保存・再生成は次行の実媒体検証へ分担する。 |
| 追加先改名の生入力・通常閉鎖の破棄と確定時の名前・保存先一致 | `SettingsDialogViewModel.SelectedCustomFolderAdditionalOutputBaseName`、`RenameSelectedCustomFolderAdditionalOutputBaseDir`、`ExecuteCloseCommand`、`CustomFolderOutputBaseRegistry.RenameLastDirectoryName`、設定の局所出力確定 | [`CompiledAdditionalOutputRename_PreservesInputSpacesUntilConfirmation`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCategoryPersistenceTests.cs) が実Bindingの逐次入力と前後空白の確定時除去、未確定での保存なしを確認する。正常確定後の同値明示改名では表示だけを正規化し、provider書込み・確認・通知・二媒体の変化なしとBMT候補の保持・確定値への非混入を確認する。別ページの通常保存では候補保持、実Close・同VM再表示では追加保存なく最新公開名へ復帰し、次の明示改名へ破棄名を混入させないことも同じ代表で確認する。`SettingDialogCustomFolderOutputBaseTests.AdditionalOutputBaseRename_RejectsCurrentDirectoryName` が `.`・空白だけの拒否を、[`OutputConfirmation_HeaderFailureUsesPublishedNamesOrFreshSavedDefinitions`](../../../BeMusicSeeker.Tests/Settings/SettingDialogOutputConfirmationTests.cs) がDB失敗後の同一runtimeとfresh owner、内部空白を含む新名のfresh provider保存値・実表参照・実出力先の一致、未知旧参照の明示再選択までの出力停止を分担する。 |
| 二媒体部分保存後の最新入力再保存と正本DB失敗 | 設定の局所出力確定、playlist header保存 | `SettingDialogEditCompletionTests.OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure`、`OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest` が今回入力の保存・生成・旧先残存後の次操作を、`BmsPlaylistMigrationAndRegistrationTests.OutputSettings_HeaderFailureKeepsAllPublishedNames` が全headerのDB確定を確認する。 |
| 未登録追加名の保持・停止、ROOT継続と明示再選択 | `CustomFolderOutputBaseRegistry`、`PlaylistPropertyDialogViewModel`、既存出力管理主体 | `CustomFolderOutputBaseRegistryTests` が純粋な解決条件を、`BmsPlaylistCustomFolderOutputTests.UnregisteredOutputBase_PreservesNameAndStopsOnlyNormalOutputUntilReselected` が既存DBの名前、正常な兄弟表、サマリー表示、プロパティ保存・ROOT切替・通常先再選択と実出力を確認する。`PlaylistSummaryBulkEditTests.BulkOutputChange_UnregisteredOldBasePreservesUnrelatedNormalOutput` が一覧の通常・追加先への再選択による実DB・必要生成と通常先の保全、外部名初期化時の停止継続を確認する。`BmsPlaylistMigrationAndRegistrationTests.ExternalTableRegistration_UnregisteredOutputBasePreservesHeaderAndCompletesBmt` は単一表の移行入口でもLR2出力だけを停止し、正本保存・BMT出力を完了することを確認する。 |
| 旧先削除失敗と新先状態、次通常処理で旧残余不変 | `BMSPlaylist` | [`RegenerateCustomFolderOutputs_UsesInjectedSettingsAndPreservesCommittedDestinationAfterOldCleanupFailure`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs): 実到達・実結果・Task終端を確認し、保持点はfinallyで解放して全開始Taskを待機する。 |
| 同一編集セッションの未完了移行・config原子的保存とC保存前の完了 | `PlaylistPropertySaveService`、`BMSPlaylist.MigrateCustomFolderOutputDirectoryWithSettings` | [`BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_UnfinishedOutputsCompleteBeforeSavingFurtherEdits`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)。DB=B、旧A/新B、ディスクから再読込みしたconfig、BMT、他表と管理外データを確認する。通常型・ルート型・追加先の正常移行は既存保存・移行テストと分担する。 |
| 種類・SQL・階層と保存設定 | [`BMSTable`](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |
| 一括生成・旧出力先・状態・受付 | [`PlaylistCustomFolderOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputOwner.cs) | [`PlaylistCustomFolderOutputOwnerTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCustomFolderOutputOwnerTests.cs)、[`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |
| 部分修復の開始・更新・全終端の要求識別 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `HydrateRequiredAsync` | [`BmsPlaylistPersistenceLifecycleTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistPersistenceLifecycleTests.cs) の `ReloadTables_HydrationRepairRegeneratesBatchPrunesStaleRowsAndPreservesCurrentFiles` と `ReloadTables_ZeroTargetRepairPublishesOnlyInactiveProgress`: 通知元`custom_folder_repair`と要求のSource`playlist_custom_folder_output_repair`、世代・親操作IDを保ち、成功・元の修復失敗・対象0件の全終端まで同じ要求IDを確認する。一括再生成・余分な行の整理・現在ファイルと利用者列の保持、既存の部分修復カウントも維持する。 |
| 通常・追加先の同一登録維持と親子配置の拒否 | [`CustomFolderOutputBaseSearchRootSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/CustomFolderOutputBaseSearchRootSyncService.cs) | [`LR2ConfigTests`](../../../BeMusicSeeker.Tests/Lr2/LR2ConfigTests.cs) の `OutputBaseSearchRootValidation_OnlyRootOutputCanContainRegisteredDirectories`、`SyncAdditionalOutputBaseRoots_RejectsParentOrChildWithoutReplacingRegistration`、`RepairNormalOutputBaseRoots_RejectsInvalidAdditionalBeforeAddingDefault`: パス方向、全候補の変更前検証、XML・登録の保持。 |
| ルート型先の復元と外側の親登録の保護 | [`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs) | [`SettingDialogCustomFolderOutputBaseTests`](../../../BeMusicSeeker.Tests/Settings/SettingDialogCustomFolderOutputBaseTests.cs) のルート同期テストと `RootOutputBaseSync_RejectsRegisteredParentWithoutChangingRootsOrFiles`: 基点・旧子登録からの復元、未生成ディレクトリの補完、禁止配置の未変更性。 |
| 注入設定による出力先移行 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) の `RegenerateCustomFolderOutputs` | [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs): 同じ実library bindingsで旧ディレクトリの移行、注入先への実生成、共有設定へのfallback不在を確認する。小さい設定providerの総呼出し回数を上位の保証にはしない。 |
| 準備中のフォルダ非保存・空入力・外部行の保存列 | [`BmsLr2SongDbSyncWorkflowRuntime`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncWorkflowOwner.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) の実準備case群: 所有libraryの生存権限をtyped bindingsへ接続し、実生成面・件数、空入力の正常終端と解放、外部フォルダ行の保存列を確認する。保存後の結果は実Queueの代表接続に分担する。 |
| スキーマの互換性・既定値 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSchemaMigrationTests.cs) |


## 関連資料

[プレイリスト保存](storage-and-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[楽曲DB同期](../integration/lr2-song-db.md)を参照します。
