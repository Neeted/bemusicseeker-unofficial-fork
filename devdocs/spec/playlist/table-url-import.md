# beatorajaのTable URL取込み

## 目的と適用範囲

beatorajaに登録した難易度表URLを、プレイリストとBMT出力の管理へ取り込む契約を定めます。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 入力と実行順

設定画面の `BeatorajaRootPath` の表示値を使い、未保存でもボタン押下時の値で実行します。`config_sys.json` の `tableURL` から文字列を順序付きで読み、空文字を除き、同じ絶対URLは先頭だけを採用します。絶対URIとして解釈できない値は失敗件数へ含めます。

確認後、既存表のページURLまたは絶対ヘッダーURLとの一致を調べます。一致する表は再取得せず順序反映だけの対象とします。未登録URLは通常の外部表取得を試み、失敗した場合だけ対応するBMTキャッシュから復元します。

外部取得は通常再読込みと同じ上限で並列化します。取得後に元URL順へ戻し、DB登録、重複名解消、表示集合、参照、URL補完、BMT出力予約をまとめて進めます。DBと表示の変更は並列化しません。進捗はURL取得だけでなく登録・参照・順序反映・完了処理を含みます。

### URLと順序の保持

成功または既存一致した表を元の `tableURL` 順でBMT順の先頭へ並べます。本アプリだけの表は現在の相対順を維持して後ろへ置きます。衝突・先頭側・無効な順序だけを必要に応じて移し、毎回全表を密に再採番して保存しません。

この入口だけは `Uri.AbsoluteUri` で正規化した文字列でなく、設定に書かれた生のURLをページURLまたは絶対ヘッダーURLとして保持します。BMT内のURLが異なっていても設定側を正本にします。既存表の補正は表の書込みロック内で行い、変更分をまとめて保存します。通常のURL指定取込みとプロパティ編集は、引き続き正規化済みURLを保存します。

### キャッシュからの復元

検索するファイル名は、生の設定URLのSHA-256＋`.bmt` です。表名、タグ、フォルダ・譜面・コースを復元し、譜面の `title`、`artist`、`md5`、`sha256`、`url`、`appendurl`、`org_md5` を引き継ぎます。直接DBへ書かず、合成したヘッダー・データJSONを通常の `LoadHeaderJSON` / `LoadDataJSON` へ渡します。

すべてのフォルダ名がタグで始まる場合はタグを互換接頭辞とし、再出力時の二重付与を防ぎます。統一されていなければ空の接頭辞とし、元のフォルダ名を保持します。

この入口では同名表を `name(1)`、`name(2)` のように改名して登録できます。通常のURL指定取込みの同名拒否とは区別します。

### 結果と出力管理

キャッシュから復元できた場合、取込み結果は「BMTから復元」の成功です。ただしサマリーのSTATUSは外部同期の結果なので、元URLの404・403・HTTP・NETWORK等の失敗を表示します。取得・復元とも失敗したURLは登録せず、管理台帳にも含めません。元のURL配列では管理外として先頭側に順序を保ちます。

取得成功した空表は登録します。BMT本体は出せなくてもURLの所有権を台帳へ残し、BMT順の対象にします。後で内容が得られたら同じIDのファイルを出し、削除・出力対象外化ではURL所有権も外します。

BMT出力を無効から有効へ切り替える際、有効なbeatorajaルートに未取込みURLがあれば先に取込みを勧める確認を出します。利用者が続行すれば出力を有効にします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 取得・既存一致・復元・生URL・重複名・失敗集計 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) |
| 順序と台帳・設定URL同期 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/Playlist/BMSPlaylist.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) |

## 関連資料

[BMT出力](bmt-export.md)、[プレイリスト保存](storage-and-export.md)を参照します。
