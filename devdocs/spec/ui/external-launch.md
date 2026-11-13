# 譜面からの外部起動

## 目的と適用範囲

一覧の右クリック操作から、Webページと外部プログラムを解決して起動する仕様です。保存する定義、単一譜面の解決条件、クリック時の再検証、失敗の表示を定めます。

あわせて、外部のプログラムやサービスへ譜面を渡す次の境界を、この文書で扱います。

| 項目 | 内容 |
| --- | --- |
| [譜面ビューア登録時のファイル送信](#譜面ビューア登録時のファイル送信) | 登録先へ送る要求の形式。 |
| [一覧再生の外部プレイヤー所有範囲](#一覧再生の外部プレイヤー所有範囲) | 一覧から起動した再生プレイヤーのプロセスを、アプリがどこまで所有するか。 |
| [BMIIDXView2015とuBMplayのINI保全](#bmiidxview2015とubmplayのini保全) | 外部プレイヤーの設定ファイルを一時的に書き換えるときの保全と復元。 |
| [LR2試聴時の設定保全](#lr2試聴時の設定保全) | LR2で試聴するときの設定XMLの一時変更と復元。 |

一覧再生の選曲・開始・停止は[再生パネル](playback-panel.md)、ファイルの原子的な保存は[ファイルとDBの整合性](../library/file-db-consistency.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| Web操作 | 譜面のハッシュをURLテンプレートへ埋め込み、ブラウザで開く右クリック操作。 |
| プログラム操作 | 譜面の絶対パスを引数テンプレートへ埋め込み、外部の実行ファイルを起動する右クリック操作。メニュー上の「プログラムから開く」。 |
| 一覧再生 | 一覧の行や再生パネルから、設定した再生方式で譜面を再生すること。プログラム操作とは別の契約を持つ。 |

## 仕様

### 保存形式と既定値

`Settings.RightClickActionsJson` は、`webActions` と `programActions` をまとめた利用者設定です。配列の順をメニューの順とし、スキーマ版は持ちません。設定値そのものが次の6件を既定値として持ち、新規設定と「既定値へ戻す」で使います。保存済みの利用者設定へ、後から追加された既定項目を自動では加えません。

| 固定ID | 名前 | URLテンプレート | 対象 |
| --- | --- | --- | --- |
| `bms-ir` | BMS-IR | `https://bms-ir.org/new/song?songmd5={md5}&view=both` | BMS |
| `mocha` | Mocha | `https://mocha-repository.info/song.php?sha256={sha256}` | 全形式 |
| `minir` | MinIR | `https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0` | 全形式 |
| `rianir` | rianIR | `https://rianir.link/ranking?sha256={sha256}` | 全形式 |
| `stellaverse-ir` | STELLAVERSE IR | `https://ir.stellabms.xyz/charts/{md5}` | 全形式 |
| `kaleid-ir` | Kaleid IR | `https://kaleidir.com/charts/{sha256}` | 全形式 |

次の保存値は、型付きの解析失敗として返します。

- null、空文字、不正なJSON
- 未知・重複の項目、重複したID
- 不正な列挙値や設定値

解析に失敗しても、起動時に例外で停止せず、既定値へ黙って戻すこともしません。利用者が明示的に修正・初期化するまで右クリック操作を公開せず、設定エラーを案内します。

Web操作とプログラム操作の名前は、日時のように見える値でもJSONの文字列として原文のまま保存・復元します。

### 編集と検証

設定画面は、永続設定とは独立した編集値を持ちます。入力と実行ファイルの選択は永続設定を変えません。項目の確定は一件ずつ検証・直列化し、既存の設定保存処理へ渡します。並べ替え・削除・既定値への復帰は操作の時点で保存し、削除と既定値への復帰は確認の後に行います（[設定](../runtime/settings.md#保存の受付と処理選択)）。取消と通常の閉じる操作は編集値を破棄します。

「既定値へ戻す」は常に使え、現在値が既定値と同じでも確認（OK/キャンセル）の後にだけ両一覧を既定値へ置き換えて保存します。項目の削除も確認の後に保存します。キャンセルでは保存も一覧の変化もありません。詳細は[設定](../runtime/settings.md#保存の受付と処理選択)に従います。不正な保存値に対する案内は、編集値が有効になるまで残し、内部の解析の文言は画面へ出しません。

| 項目 | Web操作 | プログラム操作 |
| --- | --- | --- |
| 保存する項目 | 固定ID、名前、URLテンプレート、有効設定、対象の譜面形式 | `id`、`name`、`executablePath`、`argumentTemplate`、`enabled` |
| 「追加」で作る行 | 名前とURLテンプレートが空 | 名前と実行パスが空で、引数が `{filePath}` |
| 追加直後の表示 | 選択状態で作り、両入力欄を直ちにエラー表示にする | 選択状態で作り、名前と実行パスの入力欄を直ちにエラー表示にする |

必要な項目が揃うまで保存を拒否します。

| 対象 | 受理条件 |
| --- | --- |
| Web操作の名前 | 空白でないこと。名前は通常の設定データであり、既定項目も含めて言語リソースから補完しない。 |
| URLテンプレート | 絶対HTTP(S) URLで、大小文字を区別する `{md5}` または `{sha256}` を一つ以上含むこと。未知の置換記号や閉じていない置換記号は拒否する。 |
| プログラム操作の名前 | 空白でないこと。実行ファイルを選んだとき、名前が空白の場合だけ実行ファイル名（拡張子なし）で補う。 |
| 実行パス | 指定があり、絶対パスであること。参照ボタンから選ぶ。ファイルの存在はクリック時に確認する。 |
| 引数テンプレート | 空白でなく、`{filePath}` を含み、未知・不均衡な置換記号と不均衡な二重引用符がないこと。 |

URLテンプレートへの置換では、規定の長さの16進のハッシュだけを小文字にして埋め込みます。必要なハッシュがない譜面には、その操作を公開しません。

プログラム操作の入力欄と保存時の検証は同じ規則を使い、該当欄に原因に応じた翻訳済みの説明を表示します。

- 入力を修正すると欄のエラーを更新し、言語を変更すると入力値を保ったまま説明を更新します。
- 無効にした行も保存時の検証対象です。
- 保存時に不正な行があれば、配列順で最初の不正な行を選択して保存を拒否し、保存済みの値と編集内容を保持します。

### 引数テンプレートの分解

引数テンプレートは、Windowsの二重引用符とバックスラッシュの規則で先に引数へ分解し、その後に置換します。置換したパスは再分解しません。

- 引用中の二重引用符の対は、引用状態を保ったまま一文字の `"` にします。`--label "A""B" "{filePath}"` のラベルは `A"B` として渡します。
- 引用外の空の引用区間は文字を増やさず、空の二重引用引数は一つの空引数として保持します。
- 引用符の直前のバックスラッシュは、二本ごとに一本とします。奇数本の残り一本は引用符を通常文字にし、偶数本の後の引用符は通常どおり引用状態と引用符の対を解釈します。
- 単一引用符は通常文字です。
- 閉じていない引用は拒否します。
- `{filePath}` 以外の波括弧を通常文字として書く構文と、実行ファイル名の特別な構文は設けません。

### 単一譜面の解決

`RightClickActionResolutionInput` は、一行分のMD5、SHA-256、ローカルの絶対譜面パス、判定できる場合の譜面形式を持つ変更不能な入力です。複数選択や異なる行の集約は渡しません。解決は次の規則で行い、ファイル、プロセス、ブラウザ、ダイアログには触れません。

- 有効な定義の順を保ちます。
- Web操作を、譜面形式と必要なハッシュで絞ります。形式を判定できない入力には、BMS限定・bmson限定・両対応のいずれも許可します。
- プログラム操作は、ローカルの絶対パスがある場合だけ返します。URL、相対パス、履歴のハッシュからパスを推測しません。

行の種類ごとの入力は次のとおりです。

| 行 | 入力 |
| --- | --- |
| 通常の一覧 | ファイルを開く権限があり、譜面ファイルが存在する場合だけローカルパスを渡す。 |
| 保留・導入済みのパッケージ行 | BMS/bmsonは所持状態によらず、有効なプログラム設定とローカルの絶対パスがあれば「プログラムから開く」を表示する。保留行は実際の形式を保つ。 |
| 未所持のプレイリスト行 | 表示用に合成した譜面の形式を使わず、形式不明とする。ファイルを開く権限を与えず、行にパスがあってもプログラム操作へ渡さない。 |
| ハッシュだけの行、パスの欠落、ファイルの欠落 | プログラム操作の対象外。 |
| 履歴 | 下記のとおり。 |

ハッシュだけの履歴にはWeb操作だけを表示できます。LR2の未解決履歴はBMSとして扱い、ハッシュだけではBMS/bmsonを区別できないbeatorajaの未解決履歴は形式未確定として扱います。ローカル譜面へ解決できた履歴には、既存の関連付け起動の直後にプログラムメニューを置きます。

履歴のメニュー入力は、行が取得済みの `Md5` と `Sha256` をそのまま使います。未解決のbeatorajaのSHA-256をMD5へ代用せず、ハッシュのコピー項目の有無を理由に入力を拒否しません。ローカル操作の能力と実在するパスの検査は保ちます。ハッシュのコピーは[履歴の通常列](../playlist/play-history.md#外部操作と診断)から行います。表示した履歴メニューでは、先頭・末尾・連続した区切り線を除きます。

### メニューとクリック時の起動

メニューを開くときに現在の設定で解決します。子項目には、捕捉した行やURLではなく、固定IDと操作の種類を保持します。クリック時は、メニューを開いた対象行と現在の譜面情報を取り直し、現在の設定で再解決してから起動します。複数選択中でも別の選択行に置き換えません。古い操作、設定の変更、不正な設定、譜面や実行ファイルの欠落では起動せず、翻訳済みのエラーを表示します。

| 操作 | 起動方法 |
| --- | --- |
| Web操作 | 既存のブラウザ・シェルの起動境界へ渡す。 |
| プログラム操作 | クリック時に実行ファイルと譜面の絶対パスを確認し、シェルを経由せず（`UseShellExecute=false`）、作業ディレクトリを実行ファイルの親、引数を一つずつ `ArgumentList` に設定して起動する。一回開始したら待たず、プロセスを保持しない。標準入出力の転送、生の `Arguments`、関連付け起動への代替は使わない。 |
| 関連文書 | 解決済みのパスを再確認し、一回だけシェルへ渡す。 |

起動とクリップボードへの書込みの失敗は、所有するメイン画面の入口で元の原因を診断し、一回だけ通知して止めます。クリップボードの失敗で、表示文字列やコピー対象を変えません。リリースノートと更新案内のリンクも、起動の失敗を一回案内します。更新案内は、実際の起動に成功した場合だけ肯定の結果で閉じます。通知自身が失敗しても、再起動や再通知はしません。

### 譜面ビューアの項目と登録結果の通知

通常の譜面一覧、未所持のプレイリスト項目、プレイログの3つのメニューは、譜面ビューアの項目の見出し・表示・可否とクリック時の処理を、`ScoreViewerMenuDecision` の一つの判定で決めます。各入口は選択行を譜面の種類・MD5・パス・タイトルの候補（`ScoreViewerCandidate`）へ写すだけで、判定の規則を持ちません。譜面ビューアの対象は、種類がBMSで有効なMD5（32桁の16進）を持つ譜面です。所持BMSは、対象のうち譜面ファイルが実在するものです。

| 選択 | 項目 | クリック時の処理 |
| --- | --- | --- |
| 1行で、譜面ビューアの対象（所持・未所持を問わない） | 「譜面ビューアで開く」を有効に表示する。 | ファイルが実在して未登録なら、設定「譜面ビューアへの登録時に確認メッセージを表示する」に従って1件の確認をしてから登録して開く。登録済みならそのまま開く。ファイルがなければ照会・登録をせず、MD5で開く。 |
| 2行以上で、所持BMSを1件以上含む | 「譜面ビューアに登録する」を有効に表示する。 | 所持BMSだけを照会・登録し、ビューアは開かない。新しく登録する譜面が1件以上なら、その件数を示す確認を設定によらず表示する。0件なら確認しない。 |
| それ以外（1行でBMSON・MD5なし・MD5が不正・種類不明、2行以上で所持BMSを含まない） | 表示しない。表示したまま無効にする状態は作らない。 | なし。 |

- 行数は選択行の数で数えます。譜面操作の対象へ解決できない選択行も、対象外の1行として数えます。選択行がなければ右クリックした行の1行です。プレイログは、複数選択中でも右クリックした行だけで判定します。
- プレイログの候補は、譜面へ解決できた行ならその譜面の種類・MD5・パスです。未解決のLR2の行はBMSとして履歴のMD5を使い、パスを持ちません。未解決のbeatorajaの行は形式を確定できないため、譜面情報からMD5を得ていても対象外です。ファイルが消えた解決済みのBMSの行も「開く」を有効に表示し、MD5で開きます。
- メニューを開くときは、所持BMSを1件見つけた時点で判定を打ち切り、UIスレッドで選択全件の実在を確認しません。クリック時は、その時点の選択（プレイログは右クリックした行）から同じ判定をやり直し、非表示に当たれば何もしません。同じMD5の重複は畳みません。
- 「開く」で確認を拒否した場合、登録状態の照会やアップロードに失敗した場合はビューアを開きません。失敗は失敗の通知を一回表示します。アップロードに成功した場合は成功を通知してから開きます。登録済みの譜面やファイルのない譜面を開く場合は通知しません。
- 「登録」で新しく登録した譜面がなく（全件が登録済み）、失敗もアップロードの辞退もない場合は、新しく登録する譜面がない旨の情報メッセージを一回表示します。アップロードした場合は成功の通知、失敗がある場合は失敗の通知を表示し、両方あれば両方を表示します。アップロードの確認で「いいえ」を選んだ場合は追加の通知をしません。表示の失敗は、既存の結果通知と同じく失敗の通知へ渡します。

### 譜面ビューア登録時のファイル送信

譜面ビューアへの登録では、`AppScoreViewerRegistrationGateway` が `AppHttpClient.PostFile` を使い、登録先へ `multipart/form-data` のPOSTを送ります。

| 要素 | 契約 |
| --- | --- |
| ファイル部分 | フィールド名は `file`、`filename` は譜面の元のファイル名。 |
| 非ASCIIのファイル名 | 日本語・アクセント付き文字・補助平面文字なども、MIME encoded-word、Base64、代替名へ変換せず、UTF-8の生のバイト列でヘッダーに出力する。 |
| ASCIIのファイル名とファイル本体 | バイト列をそのまま送る。 |
| `Accept` | `application/json` |
| 応答 | UTF-8で読み取り、先頭のBOMだけを除く。 |
| 成功以外のHTTP応答 | 応答本文を既存の失敗ログに含め、例外を返す。 |

応答JSONの解釈、登録の成功・拒否・例外、確認と結果の通知は、登録ワークフローが扱います。

### 一覧再生の外部プレイヤー所有範囲

一覧再生は、プログラム操作とは別の契約です。停止、次曲、終了通知、一時設定の復元に必要なため、再生プレイヤーのプロセスを保持します。ただし、外部アプリのウィンドウ状態は、継続的な正常性の判定に使いません。

#### 起動失敗の範囲

次の場合だけを起動失敗とします。

- プロセスを開始できない場合
- 起動直後に終了した場合
- プレイヤーの操作に必要なメインウィンドウを、有限時間内に確立できない場合

`PlayStart` が失敗した場合、共通のUI層はBeMusicSeeker側の再生状態と失敗通知を更新しますが、その例外だけを理由に `CloseProcess` を呼びません。開始途中のプロセスを片付ける必要があるプレイヤーは、自身の起動契約に基づいて処理します。明示的な停止、プレイヤーの置換、アプリの終了では、所有中のプロセスを終了します。

#### 必須でないデスクトップ操作

ウィンドウの前面化や装飾など、再生に必須でないデスクトップ操作は、試行するだけの操作です。OSや外部アプリが要求した状態を維持しなくても、開始済みのプレイヤーを起動失敗にしたり終了したりしません。

再生後のメイン画面の復元と表へのフォーカスも任意の表示です。Dispatcherへの予約と実際のコールバックの故障は局所の診断で止め、再生結果を変更しません。既存の画面の生存・終了保護、前面表示の条件、有限回数の確認だけを使い、故障後の再試行や高頻度の同期診断は追加しません。

#### 有界待機

外部プレイヤーの有界待機は、`ExternalPlayerWaitPolicy` の単調時計で判定します。終了待機では、期限の半分で一回だけ強制終了を試み、期限までに終了を確認できなければ失敗とします。キー送信の前後などの短い待機も同じ待機手段を使います。

### BMIIDXView2015とuBMplayのINI保全

#### 準備の順序

次の順に進め、前段が失敗した場合は後段へ進みません。

1. 既存INIの読取りと一時値の編集
2. 同じディレクトリへの一時ファイルの書込み
3. 原子的な公開
4. 開始前の同名プレイヤーの置換と、今回のプロセスの起動

別途起動された同名のプレイヤーも終了して置換しますが、INIの準備に失敗した場合は終了させません。既存INIが読取り不能な場合や、同名のディレクトリである場合は、不在として扱わず、旧バイト列を保全して、パスと原因を既存の再生失敗の経路へ返します。初回の失敗で、不完全な正式INIを公開しません。

#### INIの内容

| プレイヤー | INIがない場合 | 編集の規則 |
| --- | --- | --- |
| BMIIDXView2015 | 新規に作らず、そのまま起動する。 | - |
| uBMplay | 不足している節とキーを補って生成し、補完の保存に成功した場合はそのINIを残す。 | Shift_JISの日本語、LF/CRLFと末尾改行の有無、無関係な値とコメントを保ち、音量は0～100に制限する。 |

#### uBMplayの復元

uBMplayは、必要な節とキーが元からすべて揃っている場合に元のバイト列を保持し、所有するmainとrequestの両プロセスの終了後に、原子的に復元します。

- プロセスの生存中による復元の保留と、実際の復元の失敗を区別します。
- 復元に失敗したら退避したバイト列を保持して新規起動を阻止し、次の明示的な停止・開始で再試行します。成功した後にだけ退避を解除します。
- 失敗時の後片付けは、今回所有したプロセスだけを対象にします。
- 復元結果は退避ごとに同じ要求の中で共有し、同じ退避の失敗を再試行・重複して包装せず、次の明示操作へ引き継ぎます。旧退避の復元後に準備した新しい退避で起動に失敗した場合の後片付けも省略しません。

#### uBMplayの終了の観測と通知

uBMplayは、所有するmainプロセスの終了の確認と、終了イベントの配送を区別します。

- 次の明示的な開始が、終了済みのmainを先に検出した場合も、イベントの経路と同じ整理処理で購読・参照・ウィンドウハンドルを外してから、新しいmainを開始します。
- 遅れて配送された旧プロセスの終了通知は、新しいmainや現在のコールバックを解除しません。
- 確定した終了コールバックは、プレイヤー内部のロックを解放した後に順に配送します。個々の通知・診断の故障は、残りの配送や元の例外を変更しません。

自然終了でも、復元の後に現在の要求のコールバックへ一回だけ通知します。

- 復元の失敗は `PlaybackExitEventArgs` で再生パネルの世代付きの終了観測へ渡し、一度通知して自動の次曲送りを止めます。
- 同じ故障を最外の開始・停止要求へ返す場合は、故障を受け取る呼出元が通知を所有します（`FailureNotificationOwnedByCaller`）。終了観測は状態の解除・処理済みの判定・通知を先取りせず、呼出元が一度通知します。開始失敗では、開始側が現在の観測を終えます。通知責務の受渡しの詳細は[再生パネルの開始成功と自然終了](playback-panel.md#開始成功と自然終了)に従います。
- それ以外の古い世代の故障は、現在の曲の表示を消さず、故障自体は通知します。
- 失敗通知の終端で元の例外とINIのパスを局所診断へ残します。診断・コールバック・通知自身の故障は局所診断で止め、通知や再生結果を変更せず、プロセスのイベントから未処理の例外を返しません。外部開始のTaskをプロセスの寿命まで延長しません。

### LR2試聴時の設定保全

#### 一時変更する項目

LR2の試聴で一時的に変更するXML項目は、次の五つです。

- `system/windowsize_x`
- `system/windowsize_y`
- `system/screenmode`
- `sound/volumemaster`
- `sound/volumeflag`

設定の管理主体が保持する正本のコピーへ五項目だけを重ね、原子的に一時公開してからプロセスを開始します。外部のXMLは読み直しません。

#### 試聴中の設定の扱い

- 試聴中も、設定の読取りと設定画面の候補は通常値だけを使います。
- 初回の復元に成功するまでの通常の保存は、通常の文書を正本として採用し、実ファイルにだけ試聴用の五項目を維持します。
- 初回の復元に成功した時点で一時値の重ね書きを終えます。一時変更の管理範囲自体は、終了時の後片付けまで保持します。
- 五項目の旧値を別に退避せず、現在の正本から復元します。そのため、元の欠落した要素と親要素も再現します。
- ウィンドウ設定の後と終了時は、同じ管理主体の現在の正本を出力し、後から保存した登録と、通常値・欠落を保ちます。

#### ウィンドウの設定

LR2のウィンドウの装飾の変更は、有限時間だけ試みる操作です。通常のスタイル（`GWL_STYLE`）が試聴用の値へ収束するまで設定を試み、収束した場合だけ拡張スタイル（`GWL_EXSTYLE`）を一度設定します。拡張スタイルの読戻しの一致は成功条件にしません。時間内に装飾が収束しなくても、メインウィンドウを確立したLR2が生存していれば再生を続けます。前面化も一度だけ試みます。

#### 失敗と後片付け

| 状況 | 扱い |
| --- | --- |
| 復元の失敗 | プロセスの終了後も一時変更の管理範囲を保持し、既存のStop・Close・次の明示的な開始で復元を再試行する。未了の管理範囲を新しい試聴で覆わない。 |
| プロセス開始前の失敗 | `HasExited`、終了要求、強制終了を呼ばず、参照と購読だけを片付ける。 |
| 開始後、メインウィンドウを確立できない起動失敗 | 開始途中のプロセスを終了の対象にする。 |
| メインウィンドウの確立後の補助的な失敗 | ウィンドウ操作や設定復元の失敗だけを理由に、再生中のLR2を終了しない。明示的な終了の再試行に必要な所有状態を保持する。 |

開始と復元の失敗は、既存のプレイヤーの失敗通知へ返します。主な失敗と後片付けの失敗が重なった場合は、対象の設定パスと副次的な失敗を保持します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 既定値、厳密な解析、順序・形式・ハッシュによる解決 | [`RightClickActionSettingsStore`](../../../BeMusicSeeker/Models/ExternalActions/RightClickActionSettingsStore.cs) | [`RightClickActionSettingsStoreTests`](../../../tests/BeMusicSeeker.Core.Tests/ExternalActions/RightClickActionSettingsStoreTests.cs)、[`ApplicationSettingsMetadataTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/ApplicationSettingsMetadataTests.cs) |
| 日時に見える操作名の文字列保持 | `RightClickActionSettingsStore` | `RightClickActionSettingsStoreTests.SerializeRoundTripPreservesOrderAndDefinitions`: 原文・JSONの型と順序・定義の保持。`StoreLoadsAndSavesThroughTheProvidedSettingsOwnerWithoutFallback`: 実際の保存と新しい読込み、不正な保存後の元JSONの保持。 |
| 引数テンプレートの分解と引数列 | [`ExternalProgramArgumentTemplate`](../../../BeMusicSeeker/Models/ExternalActions/ExternalProgramArgumentTemplate.cs) | [`ExternalProgramArgumentTemplateTests`](../../../tests/BeMusicSeeker.Core.Tests/ExternalActions/ExternalProgramArgumentTemplateTests.cs): 引用内の対、引用外の空区間、空引数、バックスラッシュの奇偶、単一引用符、埋込み・反復置換、未知名・不均衡の拒否を、置換前後の引数列で確認する。 |
| 編集時の欄別検証、保存拒否と不正行の選択、言語切替、既定への復帰、取消と閉じる操作 | [`RightClickActionSettingsEditor`](../../../BeMusicSeeker/ViewModels/Settings/RightClickActionSettingsEditor.cs) | [`RightClickActionSettingsEditorTests`](../../../tests/BeMusicSeeker.Core.Tests/ExternalActions/RightClickActionSettingsEditorTests.cs)、[`SettingsDialogBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs)、[`SettingsWindowPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs)、[`SettingsWindowCompiledBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowCompiledBehaviorTests.cs) |
| 行の種類ごとのファイル条件、クリック時の再解決、プロセスの起動条件 | [`SelectedChartExternalActionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartExternalActionWorkflowOwner.cs)、[`ExternalProgramLaunchGateway`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalProgramLaunchGateway.cs) | [`SelectedChartExternalActionWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/SelectedChartExternalActionWorkflowOwnerTests.cs) の `Execute_ConfiguredProgramRechecksActionAndPassesResolvedTokens`: 通常所持と保留のBMS/bmsonで、空白を含むパスと引用符を含むラベルが起動要求まで保たれ、設定削除後は起動しないこと。`ResolveConfiguredProgram_RejectsUnavailableFileTargetsWithoutLaunching`: 未所持のプレイリスト行、ハッシュだけの行、保留ファイルの欠落、開く権限なしで起動しないこと。プロセスの起動条件は [`ExternalProgramLaunchGatewayTests`](../../../tests/BeMusicSeeker.Core.Tests/Processes/ExternalProgramLaunchGatewayTests.cs)。 |
| 単一行のメニュー、クリック時の対象の取り直し、履歴・未所持の区別 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowSelectedChartContextMenuWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) の `CompiledPendingProgramMenuLaunchesCurrentRightClickedChartWithMultipleSelection`: 複数選択中に右クリックした行と、メニュー表示後に更新されたパスで起動要求すること。`MissingPlaylistWebMenuRechecksSettingsAndKeepsOriginalTarget`: 未所持行の形式、最新の設定、右クリック対象の保持。Core側の `MissingPlaylistProjectionAllowsEveryWebChartKind`: プレイリスト投影からURLの起動要求まで。履歴との区別は [`MainWindowPlayHistoryWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlayHistoryWpfTests.cs)。 |
| 起動失敗のメッセージと多言語リソース | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowContextMenuResourceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowContextMenuResourceTests.cs) / [`MainWindowContextMenuResourceTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowContextMenuResourceTests.cs)、[`LocalizationResourceParityTests`](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs) |
| 関連文書の対象と起動失敗、更新リンクの通知 | [`SelectedChartExternalActionWorkflowOwner`](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartExternalActionWorkflowOwner.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[`UpdateAvailableDialog`](../../../BeMusicSeeker/Views/Dialogs/UpdateAvailableDialog.xaml.cs) | `SelectedChartExternalActionWorkflowOwnerTests.OpenRelatedDocument_RechecksPathAndPropagatesLauncherFailure`: パスの再確認、一回の起動、元原因の伝播。[`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `UpdateLinkLaunchFailureKeepsWindowAndDrainsCompiledEvent`: 失敗通知とウィンドウの維持。フォーカスによる閉鎖の抑止は [`MainWindowPlaybackWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaybackWpfTests.cs)、全入口の局所終端は静的確認で分担する。 |
| 履歴のハッシュ入力 | `PlayHistoryContextMenuState`、`PlayHistoryWorkflowOwner.ContextMenu` | `MainWindowContextMenuResourceTests.PlayHistoryContextMenuOwnerCreatesAssociatedTargetForResolvedRow`: 未解決履歴とパス・MD5欠落行の元のハッシュの保持とローカル操作の拒否。実メニューは[履歴の対応表](../playlist/play-history.md#実装とテストの対応)へ分担する。 |
| 譜面ビューア登録のUTF-8ファイル名、本体、応答BOM | [`AppHttpClient.PostFile`](../../../BeMusicSeeker/Ribbit/Net/AppHttpClient.cs) | [`AppHttpClientTests`](../../../tests/BeMusicSeeker.Core.Tests/Runtime/AppHttpClientTests.cs) の `PostFile_PreservesUtf8FileNameAndBytesAndStripsResponseBom`: 非ASCII名とASCII名のファイルをループバックへ送り、フィールド名・ファイル名の生バイト列、本体の一致、応答BOMだけの除去を確認する。 |
| 譜面ビューア登録の応答解釈と結果通知 | [`ScoreViewerRegistration`](../../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerRegistration.cs) | [`ScoreViewerRegistrationWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/ScoreViewerRegistrationWorkflowOwnerTests.cs)。`Accept` とHTTP失敗の経路は静的確認する。 |
| 譜面ビューアの項目の判定（3つのメニューの見出し・表示・可否とクリック時の処理）、確認と通知 | [`ScoreViewerMenuDecision`](../../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerMenuDecision.cs)、[`MainWindowScoreViewerTerminal`](../../../BeMusicSeeker/Views/MainWindow/MainWindowSelectedChartContextMenuTerminals.cs)、[`PlayHistoryContextMenuState`](../../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryContextMenuState.cs)、[`ScoreViewerRegistrationWorkflowOwner.RunAsync`](../../../BeMusicSeeker/ViewModels/ChartOperations/ScoreViewerRegistration.cs)、`WpfScoreViewerRegistrationInteraction` | [`ScoreViewerMenuDecisionTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/ScoreViewerMenuDecisionTests.cs) は1行・2行以上の判定（ファイルの有無、BMSON、MD5なし・不正、種類不明、所持BMSだけの登録対象、ファイル・選択の変化後の判定）を実ファイルで確認する。[`ScoreViewerRegistrationWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/ScoreViewerRegistrationWorkflowOwnerTests.cs) の `Open_*`・`Register_*`・`Click_*`・`WpfInteraction_SingleConfirmationSettingAppliesToOpenOnly` は、開く・登録の確認の件数と設定の適用、照会・アップロードの対象、通知の組合せ、ビューアを開くか、クリック時の判定のやり直しを確認する。[`MainWindowContextMenuResourceTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowContextMenuResourceTests.cs) は実際の履歴投影からプレイログの候補を、[`MainWindowSelectedChartContextMenuWpfTests.ScoreViewerMenu_FollowsTheSharedDecisionAndRedecidesOnClick`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowSelectedChartContextMenuWpfTests.cs) と [`MainWindowPlayHistoryWpfTests.PlayHistoryScoreViewerMenu_DecidesFromTheRightClickedRowOnly`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlayHistoryWpfTests.cs) は実メニューの見出し・表示・可否とクリックで渡す候補を確認する。 |
| 一覧再生のプロセス所有、開始失敗時の状態解除、必須でないウィンドウ操作 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`Win32ExternalPlayerWindowHost`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalPlayerWindowHost.cs) | [`PlaybackPanelViewModelTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)、[`PlaybackPanelViewModelTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlaybackPanelViewModelTests.cs)、[`ExternalPlayerProcessGatewayTests`](../../../tests/BeMusicSeeker.Core.Tests/Processes/ExternalPlayerProcessGatewayTests.cs) |
| 有界待機とキー送信 | [`ExternalPlayerWaitPolicy`](../../../BeMusicSeeker/Models/Playback/ExternalPlayerWaitPolicy.cs)、`uBMplay`、`BMIIDXView2015` | `ExternalPlayerProcessGatewayTests`: 手動の時計で期限・中止・期限の半分での一回の強制終了と終了確認。`UbmplayUsesWindowHostForAttachAndKeyMapping`: キー送信の順序と回数。実OSでの終了の観測は [`ExternalPlayerProcessGatewayTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)。 |
| INIの準備、旧バイト列の保全、開始前の置換、初回の不在 | [`BMIIDXView2015`](../../../BeMusicSeeker/Models/Playback/BMIIDXView2015.cs)、[`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs)、[`AtomicFileWriter`](../../../BeMusicSeeker/Models/Utils/FileOperations/AtomicFileWriter.cs) | `ExternalPlayerProcessGatewayTests.ExternalViewerIniFailurePreservesBytesAndDoesNotReplaceProcess`: 読取り禁止・削除共有禁止・同名ディレクトリで起動せず、旧プロセスとバイト列を保ち、明示的に再試行できること。`ExternalViewerMissingIniPreservesCreationContract`: 初回の不在。一時ファイルの書込み・公開・回収の失敗は [`AtomicFileWriterTests`](../../../tests/BeMusicSeeker.Core.Tests/FileOperations/AtomicFileWriterTests.cs)。 |
| uBMplayの一時INIの生成、値の制限、元のバイト列の復元 | [`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs) | [`UbmplaySettingsTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/UbmplaySettingsTests.cs): Shift_JIS・無関係な値・コメント・音量の範囲・補完の保持。`TemporarilyRewriteSettings_PreservesPlayerContractAndRestoresOriginalBytes`: 改行の種類と末尾改行、元のバイト列の復元。 |
| uBMplayの復元、退避の保持、終了の観測と自然終了の通知 | [`uBMplay`](../../../BeMusicSeeker/Models/Playback/uBMplay.cs)、[`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | `ExternalPlayerProcessGatewayTests` の `UbmplayRestoreFailureRetainsBackupAndBlocksNewStartUntilExplicitRetry`（復元失敗後の起動阻止と明示的な再試行）、`UbmplayReusedProcessNotifiesCurrentRequestCallback`（再利用時の現在のコールバック）、`UbmplayExitedMainStartsReplacementBeforeDelayedOldNotification`（終了済みmainの整理と遅れた旧通知の無効化）、`UbmplaySynchronousExitNotifiesOutsideOutermostLock`（ロック外での一回の通知と同じ原因）、`UbmplaySynchronousExitsKeepEverySnapshotWhenEarlierNotificationFails`（複数コールバックの順序と通知故障の分離）。`PlaybackPanelViewModelTests` の `NaturalExit_UbmplayRestoreFailureNotifiesOnceWithoutAdvancing`（一回の通知・診断、次曲へ進まないこと）、`NaturalExit_StaleFailurePreservesReplacementAndStillNotifies`（旧世代の故障で新しい表示を消さないこと）。 |
| LR2試聴の五項目、ウィンドウ設定、他のXML値、設定編集、開始・終了の失敗 | [`LR2body`](../../../BeMusicSeeker/Models/LR2/LR2body.cs)、[`Win32ExternalPlayerWindowHost`](../../../BeMusicSeeker/Models/Utils/Processes/ExternalPlayerWindowHost.cs) | [`ExternalPlayerProcessGatewayTests`](../../../tests/BeMusicSeeker.Core.Tests/Processes/ExternalPlayerProcessGatewayTests.cs)、[`Lr2PreviewOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Processes/Lr2PreviewOwnerTests.cs)、[`SettingsDialogBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs) |

## 関連資料

[設定の編集と反映](../runtime/settings.md)、[ファイル選択](file-dialogs.md)、[再生パネル](playback-panel.md)、[プレイ履歴](../playlist/play-history.md)、[原子的な保存](../library/file-db-consistency.md)。
