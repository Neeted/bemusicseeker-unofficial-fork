# beatorajaのTable URL取込み

## 目的と適用範囲

beatorajaに登録した難易度表URLを、プレイリストとBMT出力の管理へ取り込む契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

通信前の受付と必要出力の終端は[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。

### 入力と実行順

設定の確定済み `BeatorajaRootPath` を使います。別rootの候補がある場合は破棄確認し、取消・検証不成立・実P受付拒否では元候補を保持します。実受付と最終検査後にroot候補だけを確定値へ戻し、他セクションの候補は保持します。設定Windowは実取込みTaskの終端までClose・同再操作を拒否します。`config_sys.json` の `tableURL` から文字列を順序付きで読み、空文字を除き、同じ絶対URLは先頭だけを採用します。絶対URIとして解釈できない値は失敗件数へ含めます。

確認後、既存表のページURLまたは絶対ヘッダーURLとの一致を調べます。一致する表は再取得せず順序反映だけの対象とします。未登録URLは通常の外部表取得を試み、失敗した場合だけ対応するBMTキャッシュから復元します。

外部取得は通常再読込みと同じ上限で並列化します。取得後に元URL順へ戻し、DB登録、重複名解消、表示集合、参照、URL補完、BMTの実出力と終端をまとめて進めます。DBと表示の変更は並列化しません。進捗はURL取得だけでなく登録・参照・順序反映・完了処理を含みます。

#### 一致・取得・復元の分岐

一URLの取得元選択と、既存表またはDB登録候補へ振り分けるまでを示します。矢印は判定・処理順です。外部取得は上限内で並列化できますが、DB登録・参照更新・順序反映の成否はこの図に含めません。

```mermaid
flowchart TB
    Match{"既存表とURL一致"}
    Match -->|はい| Existing["既存表（再取得なし・順序反映候補）"]
    Match -->|いいえ| Fetch{"通常の外部取得に成功"}
    Fetch -->|はい| Loaded["取得成功：DB登録候補"]
    Fetch -->|いいえ| Cache{"対応BMTから復元可能"}
    Cache -->|はい| Restored["BMT復元成功：DB登録候補・外部同期失敗は保持"]
    Cache -->|いいえ| Failed["未登録・URLは管理外"]
```

BMTからの復元はこの入口で規定した経路だけです。取得成功した空表も対象で、URL所有権は[BMT出力](bmt-export.md#ファイルとurlの所有を分ける)に従います。

### URLと順序の保持

成功または既存一致した表を元の `tableURL` 順でBMT順の先頭へ並べます。本アプリだけの表は現在の相対順を維持して後ろへ置きます。衝突・先頭側・無効な順序だけを必要に応じて移し、毎回全表を密に再採番して保存しません。

この入口だけは `Uri.AbsoluteUri` で正規化した文字列でなく、設定に書かれた生のURLをページURLまたは絶対ヘッダーURLとして保持します。BMT内のURLが異なっていても設定側を正本にします。既存表の補正は表の書込みロック内で行い、変更分をまとめて保存します。通常のURL指定取込みとプロパティ編集は、引き続き正規化済みURLを保存します。

### キャッシュからの復元

検索するファイル名は、生の設定URLのSHA-256＋`.bmt` です。表名、タグ、フォルダ・譜面・コースを復元し、譜面の `title`、`artist`、`md5`、`sha256`、`url`、`appendurl`、`org_md5` を引き継ぎます。直接DBへ書かず、合成したヘッダー・データJSONを通常の `LoadHeaderJSON` / `LoadDataJSON` へ渡します。

すべてのフォルダ名がタグで始まる場合はタグを互換接頭辞とし、再出力時の二重付与を防ぎます。統一されていなければ空の接頭辞とし、元のフォルダ名を保持します。

この入口では同名表を `name(1)`、`name(2)` のように改名して登録できます。通常のURL指定取込みの同名拒否とは区別します。

### 結果と出力管理

結果は元の入力対象順に確定します。対象の参照更新警告はその対象の主結果に続け、既存一致・取得失敗・新規登録の処理段階で順序を変えません。管理表の先頭配置も成功・既存表を元対象へ対応付けた順で共通並べ替えへ渡します。

DB登録後の一括参照通知に失敗した場合、登録済み対象の成功結果と件数を維持し、同じ元例外を保持したWarningを各対象の主結果に隣接して追加します。警告のURIと表名は対応する主結果と一致し、既存一致・取得失敗の結果を置き換えません。

キャッシュから復元できた場合、取込み結果は「BMTから復元」の成功です。ただしサマリーのSTATUSは外部同期の結果なので、元URLの404・403・HTTP・NETWORK等の失敗を表示します。取得・復元とも失敗したURLは登録せず、管理台帳にも含めません。元のURL配列では管理外として先頭側に順序を保ちます。

取得成功した空表は登録します。BMT本体は出せなくてもURLの所有権を台帳へ残し、BMT順の対象にします。後で内容が得られたら同じIDのファイルを出し、削除・出力対象外化ではURL所有権も外します。

BMT出力を無効から有効へ切り替える際、有効なbeatorajaルートに未取込みURLがあれば先に取込みを勧める確認を出します。利用者が続行すれば出力を有効にします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 確定root・候補破棄確認・実受付とWindowの寿命 | [`SettingsWindow`](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs)、[`PlaylistWorkspaceViewModel.BeatorajaTableUrlImport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs) | `SettingDialogEditCompletionTests.BeatorajaTableUrlButton_DeliversConfirmedRootToWorkspace` は実buttonで確認取消/P拒否時の候補保持、確定rootの既存URL、受理後の関係候補だけの破棄、実取込みTask終端前のClose/同再操作拒否を確認する。 |
| 取得・既存一致・復元・生URL・重複名・失敗集計 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) |
| 新規2件・既存・管理外URL・残り管理表の実取込みと終端 | [`PlaylistWorkspaceViewModel.BeatorajaTableUrlImport`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs) | [`PlaylistWorkspaceExternalSourceTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs) の `StartBeatorajaTableUrlImport_NewTablePublishesOwnedProgressAndSuccessfulSummary` は正常・実一括参照通知失敗の2行を固定HTTPとfile入力から実Start・取得・登録へ通す。B/E/U/AのURIとKindはImported/Existing/Failed/Imported、失敗行ではB/Aの主結果直後に同URI・同表名・同じ元例外のWarningが続く。登録2・既存1・失敗1・復元0、警告0/2を独立期待と照合する。新readonly接続の正ID・entry所属と内容、既存表と残り二表のID・内容・相対順、DB/summary/config順、確定5表IDの管理台帳とU不在、管理外BMT bytes・他configキーを確認する。既存URLの未使用HTTPから再取得なしを観測し、通信中のBusyと要求元・操作ID・対象URI・activeからinactiveを維持する。実Startが返す取込みTaskと通知を競争させ、全gate解放後に実取込みとconsumerを回収してからHTTPとtempを破棄する。BMT形式・所有fingerprint・部分出力は既存BMT系列へ分担し、逆完了順やURL間の並列開始を要求しない。 |
| 順序と台帳・設定URL同期 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |

## 関連資料

[BMT出力](bmt-export.md)、[プレイリスト保存](storage-and-export.md)を参照します。
