# 一時ファイルの管理

## 目的と適用範囲

URL取込みとアーカイブ導入で作る一時ファイルを、利用者のファイルと区別して管理します。終了時の一括削除だけに依存せず、不要になったものから回収します。

## 用語

[共通用語集](../glossary.md)を参照します。「管理一時パス」は、現在のプロセスが `TempDirectoryPublisher` で作ったセッション配下のパスです。名前が似ているだけの一時フォルダは含みません。

## 仕様

### 所有範囲

管理一時領域は `%TEMP%\BeMusicSeeker\<session>\...` です。セッション名は `session-` で始まり、アプリが作ったことを示す印を持ちます。利用者がドロップ・選択した通常ファイル、導入済み譜面、現在のセッション外に手動で置いたファイルを、同名・同内容という理由で削除しません。

管理一時パスの判定は削除の権限を決めるものであり、入力受付の許可一覧ではありません。借用したドロップ入力の確保と受渡しは[ドロップ導入](../library/drop-install.md)に従います。

### 削除の時機

| 時機 | 回収するもの・残すもの |
| --- | --- |
| 起動時 | `Application_Startup` のミューテックス取得後、印のある過去セッションを非同期で削除する。起動は待たず、現在のセッションと印のない領域は除外する。 |
| ダウンロード後 | 管理一時領域へ保存し、展開が成功して導入入力が展開先へ切り替わった後に、管理下の元アーカイブを削除する。 |
| 展開失敗・対象なし・取消 | 保留に残らない管理下の展開先を、その場で削除する。 |
| 保留中 | 操作に必要な展開先は残す。 |
| 保留一覧から削除 | パッケージのパスが管理一時パスの場合だけ削除する。利用者所有のパスは消さない。 |
| アプリ終了 | 現在の管理一時領域を削除し、失敗しても終了を続ける。 |

### 失敗の扱い

削除は容量回収の補助処理です。失敗はログに残しますが、取得・導入・保留一覧からの削除の結果を変えません。強制終了等で回収できなかった管理領域は、次回起動時の過去セッション回収で扱います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 作成印、セッション内外、管理下にないファイルの保護 | [`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs) | [`TempDirectoryPublisherTests`](../../../BeMusicSeeker.Tests/TempDirectoryPublisherTests.cs) |
| 並列に確保した入力の寿命と一括解放 | [`temporarilyCopyFiles`](../../../BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs) | [`TemporaryCopyFilesTests`](../../../BeMusicSeeker.Tests/TemporaryCopyFilesTests.cs) |
| 展開・保留・削除に伴う寿命 | [導入入口](../library/drop-install.md)と[パッケージ変更](../library/mutations.md) | 各仕様の実入口テストで確認する。 |
| 起動・終了時の回収 | [App.cs](../../../BeMusicSeeker/App.cs) と[終了処理](../runtime/shutdown.md) | 終了仕様の対応表と、現在・過去の管理セッションを区別する実装の呼出し順で確認する。 |

## 関連資料

[ドロップ導入](../library/drop-install.md)、[URL取得](../playlist/downloads.md)、[終了](../runtime/shutdown.md)。
