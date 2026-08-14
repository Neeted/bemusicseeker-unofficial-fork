# オーディオランタイム Phase 1 現行仕様

状態: Phase 1 として採用済み
制定日: 2026-08-03
移管日: 2026-08-05
旧配置: `docs/decisions/audio-runtime-phase1.md`

## 背景

BeMusicSeeker は、再生およびオフライン音声変換のために、NuGet の ManagedBass six-package set exact `4.0.2` と、BASS、BASSWASAPI、BASSASIO、BASSmix、BASSenc、BASS_FX の各 x64 ネイティブライブラリを使用する。既存コードはデコンパイル由来であり、ネイティブランタイムのロード、デバイス列挙、バックエンド交渉、再生状態、設定の永続化、クリーンアップが static 状態に混在していた。

Phase 1 の現行依存関係セットは、managed package と6つの native DLLを一つの互換セットとして固定する。版、GetVersion、archive／DLL hash、選択した archive member、出力境界は `bass-runtime-dependency-set.md` を正本とする。登録情報、ライセンス本文、third-party notice はこの仕様へ複製しない。

## 決定事項

### 1. レビュー可能な実装単位

Phase 1 は、依存関係の順序に従って実装・レビューする。

1. ランタイム、セッション所有権、操作ゲート、クリーンアップ。
2. ASIO と WASAPI の交渉、および NullDevice のオフライン変換境界。
3. 再生／デバイステストにおける requested、negotiated、observed の契約。
4. デバイスカタログ、設定 UI、多言語化。

各単位は、次の単位をコミットする前に、その単位の所有権と失敗契約を完結させなければならない。最終統合検証は、単位ごとのレビューを代替しない。

### 1.1 現行依存関係と出力境界

ManagedBass six-package set は中央 package version と各プロジェクトの lock file で exact `4.0.2` として管理する。通常の framework-dependent build では six managed assemblies をアプリケーション出力ルートへ供給し、native DLL は `libs/x64` に配置する。single-file publish では managed assemblies を bundle し、native DLLだけを既存の `libs/x64` publish 境界へコピーする。

native DLL は `vendor/native/x64` の6ファイルを一式として所有し、アプリケーションの load／publish path も `libs/x64` に限定する。各ファイルは AMD64 PE と component-specific GetVersion を検証し、いずれかの検証またはコピーが失敗した場合は6ファイル全体を rollback unit として扱う。詳細な provenance は `devdocs/spec/bass-runtime-dependency-set.md` を参照する。

### 2. 永続的なネイティブセッション所有者を一つにする

音声ライフサイクルマネージャーは、最初のネイティブリソース取得成功からクリーンアップ確認まで、ネイティブ音声セッションを永続的に所有する唯一の主体とする。再生、デバイステスト、変換、設定の各コンシューマーは、immutable なセッショントークンまたは結果を保持してよいが、それぞれが独立したクリーンアップ保留状態を保持してはならない。

ライフサイクルマネージャーは initialize と free を直列化し、セッションが active または隔離状態にある間の再入を拒否し、解放確認できていない所有権を後続のクリーンアップ再試行用に保持する。ランタイム終了時は、まず操作ゲートを閉じ、実行中の操作完了を待ち、セッションのクリーンアップを要求し、所有権が解消された後にだけ active native generation を非公開化する。

`BassAudioRuntime.Initialize` は、native DLL の load、ManagedBass resolver installation、ManagedBass による component version validation の順に実行する。各段階の失敗は `NLogWrapper` を通して stage、component、native error source／code、runtime load 状態を記録し、native handle の解放に失敗しても最初の初期化例外を主例外として保持する。core の process-wide default-device 設定をこの bootstrap で変更・readbackして endpoint を合成してはならない。デバイス列挙および `BASS_Init` は各選択 backend の native boundary に限定する。

### 2.1 ManagedBass exact-handle bootstrap

Unit 1 以降の runtime owner は `BassAudioRuntime` とする。`BassNativeRuntime` は `libs/x64` の6つの native DLL を一つの generation としてロードし、現在 generation の filename-to-handle table を唯一所有する。`ManagedBassNativeLibraryResolver` は core、Mix、Fx、Enc、Asio、Wasapi の6つの ManagedBass assembly に一度だけ resolver を設定し、basename または `.dll` suffix の canonical name を現在 generation の exact handle へ対応付ける。resolver は handle を保存、load、free せず、未知の名前は通常の probing へ返し、既知の名前が runtime deactivation 後に別 copy へ fallback することを許可しない。

native generation の publication は6つすべての load 成功後に行い、partial load failure は当該 candidate generation の成功済み handle だけを reverse order で解放する。最初の ManagedBass DllImport binding より前に、CLR に unbind API がないことを前提として成功済み generation を process lifetime pin として保持する。shutdown は audio operation / callback admission を閉じて active root を drain した後、session と core device を解放し、active generation の publication だけを clear する。resolver の static installation lifetime、process-pinned handle lifetime、logical active publication は別であり、shutdown 後の再初期化は同じ pinned handle を current generation として再公開する。in-process の native DLL 差し替えは process restart 境界で行う。

ManagedBass `Version` properties を `BassVersionPacking` で既存の packed version 形式へ変換し、`BassNativeRuntimeTests` で pack／unpack と実 native version を検証する。runtime bootstrap に wrapper registration stage や registration material は存在せず、native smoke は通常の ManagedBass runtime owner を使用する。

### 3. クリーンアップと主例外

クリーンアップは、取得成功として記録されたレイヤーおよびハンドルに対してのみ行う。各レイヤーを解放する前に、保存済みの BASS core、WASAPI、ASIO デバイスを選択する。

デバイス選択に失敗した場合、そのレイヤーを停止または解放せず、別の current device に属する可能性があるストリームも解放しない。セッションは所有権を保持したまま隔離され、再試行対象となる。後続の再試行が成功したときに、一度だけ解放する。

冪等な stop／free 操作が返した `BASS_ERROR_INIT` は、すでに解放済みとして扱い、診断ログに記録する。これによって主となる初期化例外を置き換えない。デバイス選択失敗は、現在選択されている不明なデバイスを解放してよい根拠にはならない。

最初に発生した初期化例外または操作例外を主例外として保持する。クリーンアップ失敗は `NLogWrapper` を通して記録し、主例外を上書きせずにクリーンアップ状態として公開する。解放確認できたリソースのマネージドハンドルと状態は `finally` でリセットする。

### 4. NullDevice は可聴フォールバックではない

`NullDevice` は、明示的なオフラインエンコード／変換経路専用とする。通常再生およびデバイステストのフォールバック候補には含めず、DirectSound として報告しない。

永続化済みの旧 `NullDevice` 値を暗黙に書き換えない。設定 UI では、ユーザーが可聴バックエンドを選択するまで、利用不能な保存済みバックエンドとして表示する。通常再生またはデバイステストでこの旧値が要求された場合、ネイティブ初期化前に失敗させる。

### 5. 選択可能 backend と決定的なフォールバックマトリクス

新規の可聴出力として選択できる backend は、WASAPI shared、WASAPI exclusive、ASIO の三つだけとする。表示順および新規設定の既定値は WASAPI shared、WASAPI exclusive、ASIO の順であり、既存の enum numeric value は変更しない。`DirectSound = 0` は旧設定を読むためだけの legacy identifier で、設定 UI、カタログ、再生、デバイステスト、fallback の候補に含めない。`NullDevice = -1` は `BassAudioWriter` のオフライン変換専用である。

旧 `DirectSound` の設定を読み込んだ場合だけ、backend を WASAPI shared、endpoint を Default（identity と name は空）へ正規化する。endpoint name の一致によって旧 DirectSound 設定を移行してはならない。未知値、`INVALID`、`NullDevice` は暗黙に書き換えず、利用不能な保存値または専用経路として既存の意味を保持する。gateway、player snapshot、デバイステスト request はこの正規化境界を共有し、通常の設定保存時には正規化済みの三つ組を保存する。設定ダイアログを開いただけでは保存しない。

デバイスカタログは BASSWASAPI と BASSASIO の endpoint enumeration だけを使用し、BASS core の DirectSound／既定デバイス列挙を行わない。カタログには三つの選択可能 backend の Default placeholder と、その backend が返した endpoint だけを公開する。UI の backend index は enum の数値 cast ではなく、上記の typed policy list として扱う。

別バックエンドへ移る前に、同一バックエンド内の劣化候補を必ずすべて試す。バックエンド固有のデバイス ID を別バックエンドへ流用しない。バックエンドをまたぐ試行では、移動先バックエンドの既定エンドポイントから開始する。

- ASIO: 要求された／既定の ASIO デバイス、決定的なサンプルレート候補、Float32、次に Int16。その後、WASAPI 排他、WASAPI 共有。
- WASAPI 排他: 要求された event／period、要求 period の non-event、既定 period の non-event。その後、WASAPI 共有。
- WASAPI 共有: event mode が要求された場合はネイティブ既定 buffer／period の event mode、次にネイティブ既定 buffer／period の non-event mode。それでも失敗した場合は終了する。

WASAPI 共有では、空でないエンドポイント ID を正本とする。表示名が変わっても同じエンドポイントを失わず、古い ID が同名の別エンドポイントを選択することもない。ID を持たない旧形式の WASAPI 共有設定に限り、互換名一致を使用してよい。WASAPI 排他と ASIO は、バックエンド既定を試す前に、従来互換の名前一致移行を維持する。別 backend への試行では移動先の device identity を流用せず、移動先の Default endpoint から開始する。フォールバック理由には、ID 消失、mode／period の劣化、format／rate の正規化、ネイティブエラーの source／code、バックエンドをまたいだ移動先を記録する。

どのマトリクスも `NullDevice` で終了しない。

### 6. ASIO のフォーマットおよびサンプルレート交渉

ASIO コールバック形式と decode mixer 形式のバイト幅を常に一致させる。

1. `BASS_ASIO_FORMAT_FLOAT` を使用する Float32 decode mixer を試す。
2. Float32 変換が利用できない場合に限り、Int16 mixer と Int16 callback で再試行する。

Int8、Int24、Int32 の要求は、エンジン形式として Float32 へ正規化する。変換せずにコールバックへ直接渡してはならない。要求フォーマットと交渉済みエンジンフォーマットは、結果内で別々に保持する。

ASIO と WASAPI は callback-driven backend とする。両 backend の callback source は、
static な mixer 変数ではなく、`BassAudioSession.CallbackOutputHandle` を正本とする。
ASIO mixer は output channel の enable、join、start より前に session へ publish する。
これにより、開始処理中に callback が発火しても、session が所有する source を読む。

明示的なサンプルレートでは、要求レート、ドライバーの現在レート、重複しない標準レートの順に候補を作る。Auto では、固定の 48000 Hz ではなく、ドライバーの現在レートから開始する。受理されたレートとフォーマットを readback して、交渉結果に保存する。ManagedBass に適切な mixer 直接接続 API がない場合でも、Phase 1 では独自 P/Invoke を追加しない。

### 7. WASAPI の規則と endpoint format の境界

WASAPI の engine mixer は Float32 のままとする。engine format と endpoint format は分離する。共有モードでは、エンドポイントの mix rate と channel count、およびネイティブ既定の buffer／period を使用する。event mode が失敗した場合は、別バックエンドへ移る前に、同一バックエンド内の non-event／default-period 動作へ劣化させる。BASSWASAPI 2.4.4.1 では、共有モードのカスタム period を交渉しない。

共有モードのアプリ音量は、BASS Float32 mixer に対する gain とする。コールバックが decode data を消費するため、この gain は再生専用の `BASS_ATTRIB_VOL` channel attribute ではなく、同梱の `BASS_FX_BFX_VOLUME` mixer effect で実装する。初期 mixer gain と、セッション所有のコールバック source handle は `BASS_WASAPI_Start` より前に publish する。ASIO でも同じく `BASS_ASIO_ChannelEnable` より前に publish する。tempo graph を変更するときは、以前の stream を解放する前に、置換後の callback source を原子的に publish し、解放を確認できない場合は以前の source へ戻す。WASAPI と ASIO の callback は static mixer 変数を読まず、`BassAudioSession.CallbackOutputHandle` だけを読む。

共有モードと排他モードの初期化には別々の managed boundary を使用する。共有モードは ManagedBass の `BassWasapi.Init` に `WasapiInitFlags.Exclusive` を渡さず、event／non-event に応じて `WasapiInitFlags.EventDriven` または `WasapiInitFlags.Shared` を指定し、buffer／period は `0` としてネイティブ既定値に委ねる。排他モードは `WasapiInitFlags.Exclusive | WasapiInitFlags.AutoFormat`（event 時は `WasapiInitFlags.EventDriven` も追加）と、候補の buffer／period を渡す。共有要求に排他的な endpoint format を埋め込まず、`WASAPIPROC` と decode mixer は Float32 のままとする。

正しい共有ストリームでは、Windows が共有セッションの音量とミュートを所有し、自動的に適用する。初期化処理はこれらの値を読み取って書き戻さない。動的なアプリ音量は mixer のみに適用し、Windows のアプリ別音量スカラーおよびミュートと合成される。排他モードは、この Windows セッション制御契約の対象外である。

設定の backend dropdown、デバイステスト結果、初期化／fallback diagnostics は、numeric value と永続化値を変更せず、旧 `DirectSound` の表示も WASAPI shared として扱う。新しい表示順は WASAPI shared、WASAPI exclusive、ASIO であり、BASS core または DirectSound の名称を選択肢として公開しない。

現行の DirectSound negotiator は legacy source として残り得るが、production の selectable backend、catalog、playback、device test、fallback からは到達しない。DirectSound の旧 boundary を個別に観測する場合、その internal engine format は Float32、Windows endpoint bit depth は未観測のため `EndpointFormat = SampleFormat.UNKNOWN` とする。`UNKNOWN` は初期化失敗、fallback、デバイス選択失敗を意味せず、endpoint format が未観測であることだけを表す。現行の可聴 playback result では WASAPI または ASIO が報告する endpoint／callback format を使用する。

### 7.1 音量・ミュートの desired managed state

`DeviceVolume` と `IsDeviceMuted` は、native session の状態ではなく、アプリケーションが保持する希望値とする。runtime、session、設定ダイアログの初期化前でも設定でき、setter は native runtime のロード、デバイス列挙、session 作成、fallback、設定永続化を開始しない。

runtime admission が閉じている、shutdown 中、または cleanup quarantine 中で native operation を取得できない場合は、managed state の更新を成功させ、native への適用だけを保留する。active session が完成して `Active` になった後、WASAPI shared／exclusive と ASIO では保存済みの volume と mute から求めた effective volume（mute 中は `0f`）を適用する。WASAPI shared の初期 mixer gain、WASAPI exclusive の volume effect、ASIO の初期 mute もこの契約に従う。setter は native runtime の load、デバイス列挙、session 作成、fallback、設定永続化を開始しない。

`NullDevice` は可聴 endpoint ではなく、`BassAudioWriter` が使用するオフライン変換 graph である。そのため `IsDeviceMuted` は `NullDevice` のレンダーゲインへ適用せず、`DeviceVolume` をそのまま使用する。`NullDevice` 初期化時のレンダーゲインは既存どおり `0.4f` とし、peak／RMS normalization および normalization amplifier による `DeviceVolume` の変更も mute 状態から独立して変換結果へ反映する。`NullDevice` を初期化するために `IsDeviceMuted` を変更してはならない。

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

### 10.1 曲再生の mixer source ownership

曲の source がどの mixer に接続されているかは、`BASS_Mixer_ChannelGetMixer` の戻り値を正本として判定する。`BASS_Mixer_ChannelIsActive` は mixer 所属の判定に使用しない。未接続 source は `BASS_MIXER_CHAN_PAUSE` を指定して owning session の `MixerHandle` へ一度だけ追加し、追加後に `ChannelGetMixer` で所属を確認する。`BASS_ERROR_ALREADY` は再確認で期待 mixer が観測できた場合だけ benign race として受理し、それ以外の native error は型付き playback failure とする。

再生と一時停止は `BASS_Mixer_ChannelFlags` で pause flag を除去／設定して行い、managed `PlayState` は native 操作が成功した後にだけ変更する。source 作成前に admitted active session の core device context を選択し、その session の mixer 以外へ勝手に移動または remove してはならない。attach／detach が native readback で確認できた遷移だけを対象に、`CurrentVoices` を各一回増減させる。Play の attach 後の resume 失敗では、確認できた remove の後だけ voice count を戻し、元の failure を rollback failure で置き換えない。

自然終了 callback は session が所有する stream owner から player instance を解決し、例外を callback の外へ投げない。各再生世代は `BASS_SYNC_ONETIME` と世代番号で識別し、古い callback または古い pending cleanup が新しい再生を停止させてはならない。instance lock を取得できない場合は、世代番号を単一の atomic pending state として記録し、古い callback が新しい世代の pending cleanup を上書きしてはならない。次の Play、Pause、Stop、Dispose では current generation に一致する pending cleanup だけを再試行する。`Play` は `RESTART`、`PAUSE`、`DEFAULT` のフラグにかかわらず新しい論理再生世代を開始し、旧 end sync と旧 pending cleanup を無効化してから新しい end sync を登録する。自然終了位置は duration のまま保持する。Dispose は mixer lifecycle lock を保持して stream free より前に mixer 所属解除を試み、`ConfirmNativeStreamReleased` で session ownership、pending cleanup、voice count を解消する。Dispose と Play の競合はこの同じ instance 境界で直列化し、解放確認済みの handle を後続の Play が再利用してはならない。

再生操作の native failure は `BassAudioPlaybackException` として stage、source handle、expected／actual mixer、native error source／code、backend、session state、core device、inner exception を保持し、失敗時だけ `NLogWrapper` へ記録する。source tracking が失敗した場合は session の cleanup ownership を再取得し、解放確認できるまで handle と managed owner を保持する。`BASS_STREAM_AUTOFREE` によって session ownership を native の非同期解放へ移してはならない。

### 10.1.1 ManagedBass の player、callback、tempo、effect 境界

`BassAudioPlayer` の core stream、memory／file stream、position、length、seconds／bytes 変換、data pull、attribute、playback、end sync は ManagedBass 4.0.2 の API を使用する。memory stream の `FileProcedures` は player が強参照を保持し、read／seek callback の引数順と、EOF 位置への seek を含む native callback contract を守る。stream handle は player と session が明示的に所有し、`AutoFree` や非同期の自動解放へ所有権を移さない。

tempo graph は `BassFx.TempoCreate` と ManagedBass の tempo attributes で構成する。tempo stream の置換、mixer attach、callback source の publish、旧 stream の解放確認は既存の session ownership と同じ境界で直列化する。ASIO／WASAPI callback は引き続き `BassAudioSession.CallbackOutputHandle` だけを読み、player や static field から mixer handle を推測しない。

effect の supported set は DX8 9 種と BASS_FX 23 種の合計 32 種を維持する。persisted／application enum は ManagedBass の numeric value と共有せず、明示的な catalog mapping で `EffectType` へ変換する。ManagedBass の `IEffectParameter` が提供する型はその marshaller を使用し、native pointer を含む BASS_FX parameter は call-duration の unmanaged block と pinned array を owner-local adapter で管理して `FXSetParameters`／`FXGetParameters` に渡す。volume effect と peak EQ はこの同じ境界で初期化・更新し、parameter failure は native error と stage を記録して隠さない。

ManagedBass の `PitchShiftParameters` は FFT／oversampling が 64-bit で定義されているため使用せず、BASS_FX の legacy ABI に合わせた 32-bit の owner-local adapter を使用する。これにより pitch shift の native block は 5 フィールド、20 bytes の layout を維持する。

volume-envelope の `FXGetParameters` は、返された node count と pointer の整合性、および native block の checked byte span を検証してから全 node を直ちに managed state へコピーする。既存の managed 配列長で結果を切り捨てず、負数、過大 count、non-zero count と null pointer の組み合わせは成功扱いにしない。pointer lease の pin ownership は call boundary 内の一主体だけが持ち、lease 作成失敗または cleanup 失敗で主例外を置き換えない。

### 10.1.2 ManagedBass の encoder、metadata、writer 境界

`BassAudioWriter` は legacy helper の `BaseEncoder`／`TAG_INFO` を使用せず、project-owned の immutable `AudioTagInfo`、`AudioEncoderCommandFactory`、`AudioEncoderSession` を通して ManagedBass.Enc を使用する。command factory は shell を経由せず、executable path、output path、metadata を Windows の引数規則で quote する。WAV、LAME、Nero AAC、Opus、FLAC、OGG の six format と quality clamp／tag option は `AudioContractsTests`、`AudioEncoderCommandFactoryTests`、`BassAudioWriterTests` で検証する。アプリケーション設定上の AAC 拡張子 `.aac` と、Nero encoder が生成する実ファイル拡張子 `.m4a` は別の契約として維持する。

WAV は output path を直接 `EncodeStart` へ渡し、requested output sample format に対応する conversion flag を使用する。raw external encoder と Nero は command／header が宣言する実 source format と bytes を一致させるため、native mixer の channel info を source format の正本とする。Float32 source は LAME では signed 32-bit、FLAC／Opus では signed 24-bit へ conversion し、Ogg では `-F 3` の IEEE Float raw input、Nero では Float32 WAV header として渡す。現行 behavior に RIFF metadata がないため、WAV command へ INFO chunk を追加しない。writer は non-zero encoder handle の生成と `EncodeSetNotify` の成功後にだけ `RecordState.Playing` を公開する。stop が失敗した場合は encoder handle の ownership と Playing state を保持し、失敗を隠して解放済みとして扱わない。conversion workflow は shared audio operation lease を保持したまま writer-owned encoder の停止／dispose と結果判定を完了し、その lease を破棄してから core audio session の `BassAudioPlayer.Free` を行う。encoder cleanup 失敗時は session lease も保持して次回 cleanup で再試行する。

pull-driven render は `Bass.ChannelSeconds2Bytes`、`Bass.ChannelGetData`、`Bass.ChannelBytes2Seconds`、`Bass.ChannelGetLevel` を使用する。要求サイズより少ない data、0、`Errors.Ended` は再試行や zero-fill を行わず、そのファイルの render を自然終了させる。それ以外の native failure は channel、stage、error code を持つ `AudioWriterRenderException` として返す。level scan は data pull を先に行い、続いて同じ chunk の seconds へ変換して level を取得する。通常値は `LevelRetrievalFlags.All`、RMS は `LevelRetrievalFlags.RMS` を使用し、null／空の level や位置変換 failure を成功扱いにしない。

各 data pull が正の bytes を返した後、`AudioEncoderSession` は notify 状態と `EncodeIsActive` を確認する。encoder が停止または死亡した場合は `AudioEncoderException`（`EncoderDied`）を返して session を `Faulted` とし、残存 handle を cleanup retry の ownership として保持する。conversion workflow はファイルごとに writer disposal 後の encoder cleanup を確認し、cleanup が確認できたファイルの render failure は失敗として報告して次のファイルへ進む。cleanup が確認できない場合はバッチを停止し、render failure があればそれを primary exception として保持する。

pull-driven render、encoder の early-exit／render failure handling、conversion workflow の ManagedBass core 化は Unit 3B で完了した。ManagedBass six-package set と native six-DLL set は Unit 4 の retirement／publish acceptance を通過した final dependency contract であり、再生、backend、session、mixer、device-test、writer の production route はすべて ManagedBass API を使用する。

### 10.1.3 外部 encoder の opt-in process smoke

`ExternalAudioEncoderSmokeTests` は、利用者が別途用意した `lame.exe`、
`neroAacEnc.exe`、`opusenc.exe`、`flac.exe`、`oggenc2.exe` のうち利用可能な
ものを、明示的な `BMS_TEST_AUDIO_ENCODERS=1` のときだけ使用する
`ProcessIntegration` テストである。テスト内で生成した deterministic stereo
PCM/WAV を、NULL_DEVICE の `BassAudioWriter`、encoder creation、tag setup、
start、pull rendering、stop、cleanup へ通し、Unicode／space を含む path、既存
output の collision suffix、ファイル非空、および encoder ごとの最小 container／
frame signature を検証する。

`BMS_TEST_AUDIO_ENCODER_DIR` は production の encoder search order の先頭へ追加
され、`BMS_TEST_AUDIO_ENCODER_TYPES` は
`MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の名前 subset とする。required subset
の executable が無い場合、または subset 未指定で一つも見つからない場合は
opt-in test を fail とし、全 skip で成功扱いにしない。flag が無い通常 lane は
`Assert.Inconclusive` で終了する。encoder、license、音源は repository の
fixture／package に追加せず、失敗時も temporary file、encoder owner、audio
session を finally で cleanup し、primary failure を cleanup failure で上書き
しない。

### 10.2 デバイステストの playback failure boundary

テスト音声は、デバイス初期化、test-sound file の有無、player creation、playback start、position／stream progress、rate abnormality、natural-end timeout を別々の `AudioDeviceTestResult` failure kind として返す。player creation／playback start の型付き native failure は stage、source／expected／actual handle、native error source／code、backend、session state、core device、diagnostic reason を result とログへ引き継ぎ、設定ダイアログでは full path を表示せず、利用可能な stage と native error をローカライズされた理由へ含める。失敗結果では編集中の audio settings を変更しない。

音声テストの失敗処理は feature boundary と `SettingsDialogViewModel` で完結させ、global WPF Dispatcher の unhandled-exception suppression に依存しない。予期しない例外は `NLogWrapper` へ記録し、generic なローカライズ済みエラーを設定画面へ表示する。結果の failure kind は呼び出し側が明示し、未分類の失敗を別の progress failure へ暗黙変換しない。

### 11. ログとエラー

production の音声ログは `Ribbit/Logging/NLogWrapper.cs` を使用する。各試行について、OS／build、同梱 BASS component version、requested values、試行した backend／stage、native error source／code、negotiated values、latency、fallback destination／reason を記録する。ただし hot path に無制限なログを追加しない。

既知の音声初期化例外は、backend、stage、requested／negotiated device、native error source、native error code を保持する。曲再生およびデバイステストの playback failure は、source／expected／actual handle、session state、core device も保持して NLogWrapper へ記録する。設定 UI で表示するこれらの失敗メッセージは、対応するすべてのリソースへ多言語化する。

### 12. 実デバイス受入ゲート

以下は手動／任意の確認であり、通常の Functional lane には含めない。Windows 10 と Windows 11 の Release artifact で、少なくとも内蔵エンドポイントと、接続可能な USB または ASIO デバイス一つを使って実行する。

- WASAPI 共有: ブラウザーなど、同じエンドポイントを使う別の共有音声が再生継続できる。アプリ gain と Windows のアプリ別音量が独立しており、両方が実際の音量へ反映される。Windows のミュートで出力が消音される。
- WASAPI 排他および ASIO: negotiated device／rate／format が正確に報告され、排他的所有の制約を共有セッション音量の挙動として説明しない。
- NullDevice: 物理 endpoint を使用せず、`DeviceVolume` を render gain として使用する。`IsDeviceMuted` は変換 gain に影響しない。
- デバイステスト: `assets/audio/test.mp3` が正確に一度だけ再生され、途中で切れずに自然終了する。
- 永続化／hotplug: 明示指定したエンドポイントが Apply 後とプロセス再起動後も選択される。切断時は保存済みエンドポイントが利用不能として表示される。Default または代替エンドポイントの選択は、Apply 後にだけ永続化される。

### 13. 将来の依存関係更新を分離する

今後 BASS 一式を更新する場合は、現在の6 native DLLと managed package を一つの互換セットとして、別の reviewable change で扱う。

1. version、GetVersion、archive／DLL hash、PE machine、selected member、package lock、出力境界を同時に更新する。
2. load -> ManagedBass resolver installation -> ManagedBass version validation の bootstrap、ASIO／WASAPI callback source、Float32／Int16 negotiation、NullDevice、fallback matrix を既存契約どおり再検証する。BASS core の decode -> mixer -> PCM pull smoke test は物理デバイスなしで実行する。
3. native-boundary unit test、Functional、Full publish／update acceptance、package-layout／hash verification、clean-machine load test を実行する。
4. 内蔵、USB、ASIO デバイスで、前述の Windows 10／11 マトリクスを実行する。hotplug／default change、shared／exclusive busy state、Float32／Int16 negotiation、volume independence、fallback diagnostics、repeated cleanup を含める。
5. 以前の完全な DLL 一式を rollback unit として保持する。BASS component を一つだけ個別に rollback または配布しない。

## 互換性と対象外

- 永続化済み setting name と enum numeric value の互換性を維持する。
- 現行の managed package、native DLL、supported-version constant、hash、package metadata、application version の対応は、同一の依存関係セットとして検証する。
- Windows 10／11 および実デバイスの確認範囲は外部テストマトリクスとして記録する。unit test では、狭い native boundary と決定的な fake を使用する。
- 将来の依存関係更新では、ManagedBass six-package set とすべての BASS native component を一つの互換セットとして更新し、ABI／constant／hash／packaging をまとめて変更し、rollback と Windows／device matrix を別変更として検証する。
