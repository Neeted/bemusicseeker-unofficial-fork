# BeMusicSeeker ログ（実行記録）の見方ガイド

BeMusicSeeker を `--log-level=Info` などのオプションを付けて起動した際、あるいは `install-performance.log` 等に出力されるログには、**初期化やデータベース読み込みのパフォーマンス計測値（INFO）** や、**不正な譜面データのパース結果（WARN/ERROR）** などが記録されます。

ここでは、ログによく出現するキーワードや各項目の意味について解説します。

---

## 汎用的なキーワード

- **`start`**
  - 何らかの重い処理（検索、初期化、データベースの読み込み等）が開始されたタイミングを示します。
  - プロファイリングを行う際、この `start` のログ出力時刻から、完了を示すログが出力されるまでの「経過時間」を追うことで、どの処理がボトルネックになっているかを大まかに特定できます。

- **`〇〇Ms` (Milliseconds)**
  - その処理に要した時間（ミリ秒）を表します。（例: `1000Ms` = 1秒）

---

## 1. Everything 連携・ファイルスキャン関連

Everything を利用して BMS ファイルや関連リソースを高速検索・列挙する処理のログです。

### Everythingスキャンのログ例

```log
[INFO] everything_scan start roots=1 ext=bme;bms...
[INFO] everything_scan success bms=15000 dirs=4000 totalMs=215 ... connectMs=10 bmsQueryMs=45 ...
```

### スキャンログの項目解説

| パラメータ名                                                                  | 意味 / 何の時間か                                                                                                            |
| :---------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------- |
| **`totalMs`**                                                                 | Everything スキャン処理全体にかかった総時間（ミリ秒）                                                                        |
| **`nativeBridgeUsed`**                                                        | IPC（プロセス間通信）の高速化に特製のネイティブC++ブリッジを利用したか (`true`/`false`)                                      |
| **`nativeBridgeMs`**                                                          | ブリッジ処理を通してEverythingと通信した時間                                                                                 |
| **`connectMs`**                                                               | Everything サービスへの接続確立に要した時間                                                                                  |
| **`bmsQueryMs`**                                                              | BMS譜面本体（`*.bms` など）の検索クエリをIPC送信するのにかかった時間                                                         |
| **`bmsSearchMs`**                                                             | Everything 側でBMS譜面の検索処理そのものにかかった時間                                                                       |
| **`bmsReadMs`**                                                               | BMS譜面の検索結果をEverythingからアプリケーション側へ読み取るのにかかった時間                                                |
| **`bmsHits`**                                                                 | 見つかったBMS譜面の件数                                                                                                      |
| **`siblingQueryMs`<br>`siblingSearchMs`<br>`siblingReadMs`<br>`siblingHits`** | BMSと同じ階層にある「関連リソース（音源、BGA映像、画像など）」の検索クエリ送信、検索処理、読み取り時間、および見つかった件数 |
| **`buildResultMs`**                                                           | C#側で見つかった全パスのメモリリストを構築し、内部データ化するのにかかった時間                                               |
| **`hashBuildMs`**                                                             | 高速なファイル検索（スマート上書きやハッシュ探索等）のために、ディレクトリごとのハッシュテーブルを構築する時間               |

---

## 2. 楽曲データベース (SQLite) 読み込み関連

LR2 の `song.db` および独自のメンテナンス情報の読み込みを行う処理のログです。
数万〜十数万件の譜面を持つ環境において、ここの時間が起動速度に直結します。

### DB読み込みのログ例

```log
[INFO] song_tbl_load_io song_read_ms=8460 song_count_ms=8 song_materialize_ms=8451 song_count=196713 maintenance_read_ms=3007 ... db_write_ms=0
```

### DBロードログの項目解説

| パラメータ名                     | 意味 / 何の時間か                                                                                                         |
| :------------------------------- | :------------------------------------------------------------------------------------------------------------------------ |
| **`song_read_ms`**               | `song.db` の BMS楽曲テーブルからデータを読み込む処理にかかった総時間                                                      |
| **`song_count_ms`**              | 読み込む前に「何件あるか」をDBから取得（Count）するのに要した時間                                                         |
| **`song_materialize_ms`**        | DBから読み込んだ生のデータ行を、C# の `song` オブジェクト群に変換（マテリアライズ）してメモリ上に乗せる作業にかかった時間 |
| **`song_count`**                 | DBから読み込んだ総譜面数                                                                                                  |
| **`maintenance_read_ms`**        | 独自のメンテナンス・追加管理データ（ハッシュや導入状態など）を読み込む時間                                                |
| **`maintenance_materialize_ms`** | メンテナンスデータを C# のオブジェクトに変換する時間                                                                      |
| **`maintenance_count`**          | 読み込んだメンテナンスデータの総数                                                                                        |
| **`folder_read_ms`**             | 各譜面が格納されている「フォルダ（パス）」情報をDBから読み込む時間                                                        |
| **`db_write_required`**          | 読み込み時にパスの不整合などが見つかり、DBへの書き込み修正（Update）が必要と判定されたか (`true`/`false`)                 |
| **`db_write_ms`**                | （上記が `true` の場合）DBへの保存書き込みに要した時間                                                                    |

---

## 3. アプリケーション初期化の進行度合い

### 初期化フェーズのログ例

```log
[INFO] init_stage start scope=ReloadFiles
[INFO] init_stage CheckDb scope=Initialize
```

### 初期化ログの項目解説

- **`init_stage`**
  - アプリケーション起動時の各フェーズ処理状況を示します。
  - **`scope`**: 現在実行中の大枠の処理名（例: `Initialize` = 全体初期化, `ReloadFiles` = 難易度表等の再読み込みタスク）
  - 前述の通り、「`start`」という記載がある場合は「あるステップの初期化処理を開始した」タイミングを表します。ここで処理が詰まっている場合、次のログが出るまでの時間差が大きくなります。

---

## 4. UI更新抑制状況 (`ui_suppress`) 関連

大量のデータを処理する際、画面の描画更新（UIフリーズや無駄なレンダリング）を抑えて処理速度を向上させるための内部機能のログです。

### `ui_suppress` のログ例

```log
[INFO] ui_suppress begin depth=1 mask=All suppressed=All
[INFO] ui_suppress pending depth=1 channel=LibraryFolderTree pending=LibraryFolderTree
[INFO] ui_suppress flush_install_tree_ms=... flush_total_ms=45 deferred_library_folder_tree=True
[INFO] ui_suppress end depth=0 flush=All
```

### `ui_suppress` の項目解説

| パラメータ名 / メッセージ | 意味 / 何の時間か                                                                                                                                                    |
| :------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **`begin` / `end`**       | UI更新の抑制を開始 (`begin`) したタイミングと、解除 (`end`) したタイミングを示します。                                                                               |
| **`depth`**               | 抑制のネスト（重なり）の深さです。`depth=1` 以上でUI更新が止まり、`depth=0` に戻った時点で溜まっていた更新が一斉に放たれます。                                       |
| **`mask` / `suppressed`** | どの画面要素の更新を止めるかの対象範囲（`LibraryFolderTree`, `LibraryMainView` など）です。                                                                          |
| **`pending`**             | UI抑制期間中に、裏側で「更新要求」が発生し、保留（Pending）状態になった画面要素のリストです。                                                                        |
| **`flush_total_ms`**      | UI抑制が解除（`end`）され、保留されていた画面更新（Flush）を実際に描画するのに一気に処理した総時間（ミリ秒）です。各画面ごとの内訳 (`flush_..._ms`) も出力されます。 |

---

## 5. `[WARN]` / `[ERROR]` ログの主な出力内容

エラー（`ERROR`）や警告（`WARN`）レベルのログには、単なるスタックトレースだけでなく、問題解決に役立つ具体的なコンテキスト（ファイル名や行番号など）が付与される場合があります。代表的なものは以下の通りです。

### ネットワーク・通信関連 (`Ribbit.Net.GZipWebClient`)

```log
[WARN] http_request_failed method=GET url=... statusCode=403 status=Forbidden webStatus=ProtocolError
```

- **`method` / `url` / `statusCode` / `status` / `webStatus`**
  - 難易度表などのダウンロードに失敗した際の詳細なHTTPレスポンス状況が出力されます。`statusCode=403` などが出た場合は、アクセス拒否（User-Agent弾きや非公開化）が原因であると特定できます。

### オーディオ・再生エンジン関連 (`Ribbit.Media.BassAudioPlayer`)

```log
[WARN] Initialize WASAPI(EX) driver failed: ...
[WARN] BASS_Mixer_StreamAddChannel failed: BASS_ERROR_FILEOPEN : C:\BMS\song\audio.wav
[ERROR] Ogg Decode failed: ...
```

- 音声再生ライブラリ（BASS）の内部エラーです。
- **`Initialize ... driver failed`**: オーディオデバイス（ASIO, WASAPI, DirectSound）の初期化失敗。設定画面のオーディオ出力設定とPCの環境が合っていない可能性があります。
- **`BASS_... failed: [Error Code] : [FileName]`**: 特定の音声ファイル（WAVやOGGなど）の読み込みや再生に失敗した場合。ファイルが破損しているか、BASSが対応していないフォーマットの可能性があります。

### BMS譜面の構文解析（パーサー）関連 (`Ribbit.BMS.BMSFile`)

BMSの構文解釈時、記述ルールから外れた不正な譜面を検出した際に数多くの警告が出力されます。譜面制作者や、譜面の仕様バグを調べる際に非常に有用です。

| ログの出力例 / メッセージ                                                                        | 原因・意味                                                                                                                                                                                                   |
| :----------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Invalid #RANDOM range l:10 #RANDOM 0`                                                           | `#RANDOM` などの制御文で、指定された値（パラメータ）が不正、または範囲外である（行番号 `l:xx` 付きで出力されます）。他に `#BPM`, `#TOTAL`, `#PLAYLEVEL` などでも発生します。                                 |
| `Duplicate note is moved to BGM, Mes:xx Pos:xx Ch:xx Idx:xx`<br>`Duplicate note is removed, ...` | 全く同じタイミング・同じレーンに複数のノーツが重複して配置されている不正な状態。BGMレーンへ移動させるか、削除してパースを続行したことを示します。小節(`Mes`), 位置(`Pos`), チャンネル(`Ch`) が記載されます。 |
| `LN start note is merged and moved to BGM...`<br>`Isolated LN end note is removed...`            | ロングノーツ（LN）の終点がない、あるいは始点がない不正な配置（孤立LN）を検出して自動修正したことを示します。                                                                                                 |
| `NO NOTE EXISTS`                                                                                 | 譜面内にノーツが1つも存在しません（0ノーツ譜面）。                                                                                                                                                           |
| `#BPM is not defined, set BPM=130`                                                               | 初期BPMが定義されていなかったため、デフォルトの130が適用されました。                                                                                                                                         |
| `#BPMxx not found.`                                                                              | 途中でBPM変更ノーツ（`#BPMxx`）が置かれていますが、対応するBPM定義が存在しません。                                                                                                                           |
| `[ERROR] Arithmetic exception occuered on a fraction multiplying.`                               | 拍子の計算などにおいて、0除算などの算術エラー（仕様外の極端な拍子変更など）が発生しました。                                                                                                                  |
