# オーディオランタイム Phase 1 現行仕様

状態: Phase 1 として採用済み
制定日: 2026-08-03
移管日: 2026-08-05
旧配置: `docs/decisions/audio-runtime-phase1.md`

## 背景

BeMusicSeeker は、再生およびオフライン音声変換のために、同梱の Bass.Net と、BASS、BASSWASAPI、BASSASIO、BASSmix、BASSenc、BASS_FX の各ネイティブライブラリを使用する。既存コードはデコンパイル由来であり、ネイティブランタイムのロード、デバイス列挙、バックエンド交渉、再生状態、設定の永続化、クリーンアップが static 状態に混在していた。

Phase 1 では、Bass.Net、各ネイティブ DLL、対応バージョン定数、ハッシュを変更せずに、これらの挙動を安定化する。マネージド／ネイティブ BASS 一式の更新は Phase 2 とし、必ず別の変更として扱う。

## 決定事項

### 1. レビュー可能な実装単位

Phase 1 は、依存関係の順序に従って実装・レビューする。

1. ランタイム、セッション所有権、操作ゲート、クリーンアップ。
2. ASIO、WASAPI、DirectSound の交渉。
3. 再生／デバイステストにおける requested、negotiated、observed の契約。
4. デバイスカタログ、設定 UI、多言語化。

各単位は、次の単位をコミットする前に、その単位の所有権と失敗契約を完結させなければならない。最終統合検証は、単位ごとのレビューを代替しない。

### 2. 永続的なネイティブセッション所有者を一つにする

音声ライフサイクルマネージャーは、最初のネイティブリソース取得成功からクリーンアップ確認まで、ネイティブ音声セッションを永続的に所有する唯一の主体とする。再生、デバイステスト、変換、設定の各コンシューマーは、immutable なセッショントークンまたは結果を保持してよいが、それぞれが独立したクリーンアップ保留状態を保持してはならない。

ライフサイクルマネージャーは initialize と free を直列化し、セッションが active または隔離状態にある間の再入を拒否し、解放確認できていない所有権を後続のクリーンアップ再試行用に保持する。ランタイム終了時は、まず操作ゲートを閉じ、実行中の操作完了を待ち、セッションのクリーンアップを要求し、所有権が解消された後にだけネイティブモジュールをアンロードする。

### 3. クリーンアップと主例外

クリーンアップは、取得成功として記録されたレイヤーおよびハンドルに対してのみ行う。各レイヤーを解放する前に、保存済みの BASS core、WASAPI、ASIO デバイスを選択する。

デバイス選択に失敗した場合、そのレイヤーを停止または解放せず、別の current device に属する可能性があるストリームも解放しない。セッションは所有権を保持したまま隔離され、再試行対象となる。後続の再試行が成功したときに、一度だけ解放する。

冪等な stop／free 操作が返した `BASS_ERROR_INIT` は、すでに解放済みとして扱い、診断ログに記録する。これによって主となる初期化例外を置き換えない。デバイス選択失敗は、現在選択されている不明なデバイスを解放してよい根拠にはならない。

最初に発生した初期化例外または操作例外を主例外として保持する。クリーンアップ失敗は `NLogWrapper` を通して記録し、主例外を上書きせずにクリーンアップ状態として公開する。解放確認できたリソースのマネージドハンドルと状態は `finally` でリセットする。

### 4. NullDevice は可聴フォールバックではない

`NullDevice` は、明示的なオフラインエンコード／変換経路専用とする。通常再生およびデバイステストのフォールバック候補には含めず、DirectSound として報告しない。

永続化済みの旧 `NullDevice` 値を暗黙に書き換えない。設定 UI では、ユーザーが可聴バックエンドを選択するまで、利用不能な保存済みバックエンドとして表示する。通常再生またはデバイステストでこの旧値が要求された場合、ネイティブ初期化前に失敗させる。

### 5. 決定的なフォールバックマトリクス

別バックエンドへ移る前に、同一バックエンド内の劣化候補を必ずすべて試す。バックエンド固有のデバイス ID を別バックエンドへ流用しない。バックエンドをまたぐ試行では、移動先バックエンドの既定エンドポイントから開始する。

- ASIO: 要求された／既定の ASIO デバイス、決定的なサンプルレート候補、Float32、次に Int16。その後、WASAPI 排他、WASAPI 共有、DirectSound。
- WASAPI 排他: 要求された event／period、要求 period の non-event、既定 period の non-event。その後、WASAPI 共有、DirectSound。
- WASAPI 共有: event mode が要求された場合はネイティブ既定 buffer／period の event mode、次にネイティブ既定 buffer／period の non-event mode。その後、DirectSound。
- DirectSound: 要求デバイス、次にバックエンド既定デバイス。それでも失敗した場合は終了する。

WASAPI 共有では、空でないエンドポイント ID を正本とする。表示名が変わっても同じエンドポイントを失わず、古い ID が同名の別エンドポイントを選択することもない。ID を持たない旧形式の WASAPI 共有設定に限り、互換名一致を使用してよい。WASAPI 排他、ASIO、DirectSound は、バックエンド既定を試す前に、従来互換の名前一致移行を維持する。フォールバック理由には、ID 消失、mode／period の劣化、format／rate の正規化、ネイティブエラーの source／code、バックエンドをまたいだ移動先を記録する。

どのマトリクスも `NullDevice` で終了しない。

### 6. ASIO のフォーマットおよびサンプルレート交渉

ASIO コールバック形式と decode mixer 形式のバイト幅を常に一致させる。

1. `BASS_ASIO_FORMAT_FLOAT` を使用する Float32 decode mixer を試す。
2. Float32 変換が利用できない場合に限り、Int16 mixer と Int16 callback で再試行する。

Int8、Int24、Int32 の要求は、エンジン形式として Float32 へ正規化する。変換せずにコールバックへ直接渡してはならない。要求フォーマットと交渉済みエンジンフォーマットは、結果内で別々に保持する。

明示的なサンプルレートでは、要求レート、ドライバーの現在レート、重複しない標準レートの順に候補を作る。Auto では、固定の 48000 Hz ではなく、ドライバーの現在レートから開始する。受理されたレートとフォーマットを readback して、交渉結果に保存する。同梱 Bass.Net に適切な mixer 直接接続 API がない場合でも、Phase 1 では独自 P/Invoke を追加しない。

### 7. WASAPI と DirectSound の規則

WASAPI の engine mixer は Float32 のままとする。engine format と endpoint format は分離する。共有モードでは、エンドポイントの mix rate と channel count、およびネイティブ既定の buffer／period を使用する。event mode が失敗した場合は、別バックエンドへ移る前に、同一バックエンド内の non-event／default-period 動作へ劣化させる。同梱 BASSWASAPI 2.4.1 では、共有モードのカスタム period を交渉しない。

共有モードのアプリ音量は、BASS Float32 mixer に対する gain とする。コールバックが decode data を消費するため、この gain は再生専用の `BASS_ATTRIB_VOL` channel attribute ではなく、同梱の `BASS_FX_BFX_VOLUME` mixer effect で実装する。DirectSound も同じ mixer-effect 経路を使用する。初期 mixer gain と、セッション所有のコールバック source handle は `BASS_WASAPI_Start` より前に publish する。tempo graph を変更するときは、以前の stream を解放する前に、置換後の callback source を原子的に publish する。

共有モードと排他モードの初期化には別々の managed boundary を使用する。共有モードは sample-format 引数を持たない Bass.Net overload を呼び、`BASS_WASAPI_EXCLUSIVE` および `BASS_WASAPI_AUTOFORMAT` を決して渡さない。同梱の explicit-format overload は `BASS_WASAPI_EXCLUSIVE` を注入するため、明示的な排他経路だけで使用する。共有要求に排他的な endpoint format を埋め込まず、`WASAPIPROC` と decode mixer は Float32 のままとする。

正しい共有ストリームでは、Windows が共有セッションの音量とミュートを所有し、自動的に適用する。初期化処理はこれらの値を読み取って書き戻さない。動的なアプリ音量は mixer のみに適用し、Windows のアプリ別音量スカラーおよびミュートと合成される。排他モードは、この Windows セッション制御契約の対象外である。

DirectSound カタログ項目では、descriptor と元のネイティブ index を対で保持する。ネイティブデバイス index 0、disabled 項目、no-sound 項目は、選択可能な可聴デバイスに含めない。既定デバイス要求では、対応している場合は device `-1` を使用し、初期化後に実際に選択されたデバイスを記録する。

Phase 1 では `BASS_DEVICE_DSOUND` をハードコードせず、WPF window handle が根本原因だと仮定して `IntPtr.Zero` を変更しない。将来 BASS を更新するときは、DirectSound 経路で `BASS_DEVICE_DSOUND` を使用する必要性を評価する。

### 8. Requested、negotiated、observed の値

- Requested values は、ユーザーが要求した backend、device identity／name、rate、format、buffer、event mode、volume の immutable snapshot とする。
- Negotiated values は、ネイティブ API が受理または報告した backend、device、rate、channels、engine format、endpoint format、mode、latency とする。
- Observed values は、stream progress、wall-clock duration、playback-position duration、progress ratio、初期化とストリーム進行がそれぞれ独立に確認されたか、といったランタイム測定値とする。

`Actual` を使う既存の内部名は、negotiated と observed のどちらを表すかをドキュメントで明示する場合に限り維持してよい。利便性だけを理由に新たな並行 alias を追加しない。

通常再生では negotiated values を永続化しない。設定ダイアログは、backend／device-identity／device-name の immutable draft を所有する。カタログ更新や WPF の一時的な selection change は、この三つ組をアプリケーション設定へ書き込まない。Apply は三つ組全体をまとめて永続化し、Cancel は保存済み draft を復元する。保存に失敗した場合は、draft を再試行可能なまま保持しつつ、メモリ上の設定三つ組を以前の値へ戻す。デバイス ComboBox は、カタログ更新依存の配列 index ではなく、選択されたカタログオブジェクトへ bind する。

設定のデバイステストが編集中の値を更新するのは、要求がまだ最新であり、初期化とストリーム進行が成功し、フォールバックも正規化も発生せず、明示指定された backend／device／rate／format が negotiated values と一致した場合だけとする。Default device および Auto rate／format の意図は、一時的な具体値へ置換せず、Default／Auto のまま保持する。

### 9. 古いデバイス情報とカタログ更新

カタログ更新は明示的かつ反復可能とする。あるバックエンドの列挙失敗によって、別バックエンドの結果を破棄しない。更新に失敗したバックエンドでは、直近の正常な一覧を保持する。

直近の正常なカタログに保存済みデバイスが存在しない場合、Default placeholder とは別の「利用不能な保存済み項目」として表現する。更新によって保存済み identity を消去または書き換えない。Default を明示的に選択した場合は保存済みデバイスを消去し、別エンドポイントを選択した場合は置き換える。

カタログ項目では、stable identity、display name、native index、default status、availability を区別する。非同期更新では、現在選択されているバックエンドに対する最新要求だけを publish する。

### 10. デバイステストの進行判定

初期化成功とストリーム進行を別々の結果として扱う。再生進行は `Stopwatch` と playback position で測定する。

評価処理は、最初に playback position が前進するまでの startup pre-roll を無視し、その後、wall-clock で少なくとも 1 秒の測定区間を要求する。position は単調増加し、再生は進行し続け、playback-position／wall-clock 比は `0.75` 以上 `1.25` 以下でなければならない。1.5 倍速および 2 倍速は失敗とする。この区間の通過は診断上の節目であり、player lifetime の終端ではない。可聴テストに成功した場合でも、有限長のテスト音が自然終了するまで player とネイティブセッションを保持する。

テスト音は正確に一度だけ再生する。初期化済み output session は、その再生が自然終了するまで維持する。固定された操作時間を満たすために短い音源を繰り返さない。

停止したストリームを自然終了とみなすのは、playback position が報告済み duration に到達している場合だけとする。テスト音源がない、進行しない、逆行する、比率が範囲外、早期終了する、自然終了へ到達しない、例外が発生する、のいずれかでテストを失敗させ、設定変更を行わない。結果は、物理的に音が聞こえたとは主張しない。デバイス初期化とストリーム進行を独立して報告する。

### 11. ログとエラー

production の音声ログは `Ribbit/Logging/NLogWrapper.cs` を使用する。各試行について、OS／build、同梱 BASS component version、requested values、試行した backend／stage、native error source／code、negotiated values、latency、fallback destination／reason を記録する。ただし hot path に無制限なログを追加しない。

既知の音声初期化例外は、backend、stage、requested／negotiated device、native error source、native error code を保持する。設定 UI で表示するこれらの失敗メッセージは、対応するすべてのリソースへ多言語化する。

### 12. 実デバイス受入ゲート

以下は手動／任意の確認であり、通常の Functional lane には含めない。Windows 10 と Windows 11 の Release artifact で、少なくとも内蔵エンドポイントと、接続可能な USB または ASIO デバイス一つを使って実行する。

- DirectSound: アプリ gain と Windows のアプリ別音量が独立しており、両方が実際の音量へ反映される。
- WASAPI 共有: ブラウザーなど、同じエンドポイントを使う別の共有音声が再生継続できる。アプリ gain と Windows のアプリ別音量が独立しており、両方が実際の音量へ反映される。Windows のミュートで出力が消音される。
- WASAPI 排他および ASIO: negotiated device／rate／format が正確に報告され、排他的所有の制約を共有セッション音量の挙動として説明しない。
- デバイステスト: `assets/audio/test.mp3` が正確に一度だけ再生され、途中で切れずに自然終了する。
- 永続化／hotplug: 明示指定したエンドポイントが Apply 後とプロセス再起動後も選択される。切断時は保存済みエンドポイントが利用不能として表示される。Default または代替エンドポイントの選択は、Apply 後にだけ永続化される。

### 13. Phase 2 の移行計画を分離する

Phase 2 は、このコードのみの Phase 1 が実デバイスで受け入れられた後に行う、独立したネイティブ依存関係変更とする。

1. Bass.Net と BASS、BASSmix、BASSWASAPI、BASSASIO、BASSenc、BASS_FX について、一つの互換リリースセットを選定し、上流の version および license reference を保存する。
2. managed assembly とすべての x86／x64 native component をまとめて更新し、version constant、supported-version check、hash、package layout、third-party notice も同じ commit series で更新する。
3. 必要に応じて互換性コメントを新しい ABI surface に置き換える。これには、DirectSound 用 `BASS_DEVICE_DSOUND` の評価、および上流 header と signature を照合した後にだけ新たに公開された managed API を使うことを含む。
4. デバイステスト前に、native-boundary unit test、Functional、Full publish／update acceptance、package-layout／hash verification、clean-machine load test を実行する。
5. 内蔵、USB、ASIO デバイスで、前述の Windows 10／11 マトリクスを実行する。hotplug／default change、shared／exclusive busy state、Float32／Int16 negotiation、volume independence、fallback diagnostics、repeated cleanup を含める。
6. 以前の完全な DLL 一式を rollback unit として保持する。BASS component を一つだけ個別に rollback または配布しない。

## 互換性と対象外

- 永続化済み setting name と enum numeric value の互換性を維持する。
- Phase 1 では、native DLL、Bass.Net、supported-version constant、hash、package metadata、application version を変更しない。
- Windows 10／11 および実デバイスの確認範囲は外部テストマトリクスとして記録する。unit test では、狭い native boundary と決定的な fake を使用する。
- Phase 2 では、Bass.Net とすべての BASS native component を一つの互換セットとして更新し、ABI／constant／hash／packaging をまとめて変更し、rollback と Windows／device matrix を別変更として検証する。
