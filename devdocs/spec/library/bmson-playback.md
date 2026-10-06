# bmsonの内蔵再生と音声書出し

## 目的と適用範囲

bmsonの音声を、BMSと共通の事前復号、発音予約、内蔵プレイヤー、先読み、音声書出しへ接続する契約です。1.0.0と旧0.21を対象とし、通常のbeatoraja・Bemuse向け譜面の音声プレビューに必要な互換性を扱います。特定プレイヤーの採点・不正入力処理・PCMの完全再現、BGA表示は保証しません。

`chart_info` 用の解析とは別の経路です。再生用解析の受理条件や時刻を、既存の[譜面情報用パーサー](parser-compatibility.md)とDB保存値へ適用しません。BMSの[時刻計算](ribbit-timing.md)と[発音規則](../runtime/audio.md#bms音声のframe予約)は維持します。採用理由と実譜面の測定は[設計判断](../../decisions/bmson-playback-boundaries.md)に分けます。

## 用語

共通語義は[用語集](../glossary.md)に従います。

| 用語 | 意味 |
| --- | --- |
| pulse | 曲の先頭を0とする整数位置。`resolution` は四分音符当たりのpulse数です。小節線で位置をリセットしません。 |
| 固定刻み時刻 | 100nsの1tickを `2^32` 分割した刻みの整数値。曲全体の累積値を `BigInteger` で保持します。 |
| 制御境界 | BPM変更またはSTOPの位置。時刻計算の区間を分ける位置です。 |
| 同位置の組 | 一つの音源チャンネル内で `y` が等しいノートの集合。音源の再開・継続を一回解決します。 |
| 論理音声区間 | 解析器が同位置の組ごとに作る曲内開始、音源内開始、有限終端またはEOFです。判定位置や音切りの情報であり、一件ずつnative voiceを作る指示ではありません。 |
| 継続発音列 | 元の音源チャンネルの一回の再開と、そこから続く `c=true` の組。全自動再生では一つの連続voiceとして出力します。 |

## 仕様

### 形式の選択と入力の正規化

明示 `version="1.0.0"` は新形式、`version="0.21"` は旧形式として読みます。版が欠落している場合は旧名の存在により旧形式を選び、それ以外の既知の新形式を新形式として読みます。未知の版でも既知の新形式の構造を読めれば、そのフィールドを処理します。将来版全体への対応は保証しません。明示 `version=null` は入力エラーです。

旧形式の `soundChannel`、`info.initBPM`、`bpmNotes[].v`、`stopNotes[].v` を共通の入力へ正規化します。旧形式の `resolution` は240固定です。同じ意味の新旧名が混在した場合は、新名が存在すればその値を優先します。新名の値がnullでも旧名へ戻さず、そのフィールドのnull規則を使います。配列の連結による発音の二重化は行いません。

| 入力 | 既定・条件 |
| --- | --- |
| 新形式の `resolution` | 欠落・null・0は240。負値は整数として絶対値を取り、固定幅の符号反転で溢れさせません。 |
| 初期BPM | 正の有限値を必須とします。欠落・非正値・非有限値を120などへ補いません。 |
| 表示文字列・`subartists` | 欠落・nullは空文字列・空配列。 |
| 新形式の `total` / `mode_hint` | 既定は100 / `beat-7k`。旧形式のtotalは新形式への比率換算をせず表示値として保持します。 |
| 任意のイベント配列・音源チャンネルの `notes` | 欠落・nullは空配列。 |
| `lines` | 欠落・nullは四拍ごとの表示線。明示空配列は線なし。明示 `y=0` は初期線情報として重複も保持し、CurrentMeasure・LastMeasureへ加算しません。正pulse線は時刻0へ量子化されても加算します。いずれも音声時計を変えません。 |
| ノートの `x` / `l` / `c` / `up` | `x` の欠落・null・0はBGM、`l` 欠落は0、`c`・`up` 欠落はfalse。`y`・`l` は非負整数です。 |

JSONの有限十進値は、数値字句の係数と指数から厳密な有理数へ変換します。有効な有限JSON数値の字句を持つ数値文字列も受理します。二進浮動小数へ変換した値を計算や比較の入力へ戻しません。正の有限十進値は、`double` の範囲を超えるという理由だけでは拒否しません。仮数の全数字は最大4096桁、明示十進指数の値は `[-4096,4096]` に限り、BigInteger係数・べき乗生成前に検査します。符号・小数点は桁数から除き、0・末尾0は数えます。指数の先頭0は数値として扱い、追加の字句長上限は設けません。数値と数値文字列は同条件です。0係数も先に上限を検査し、その後はべき乗を作らず0とします（`0e5000` は拒否）。scaleを差し引いた実効指数へ別の±4096上限は置きません。上限違反は元原因付き `InvalidBmsonFileException` とし、clampや既定値へ変換しません。`1e1000` は引き続き受理し、範囲内でも必要な実時刻が表現範囲を超えれば失敗します。この上限は `Fraction`、BMS数値、固定刻み時計、DB用 `chart_info` の受理条件へ波及させません。

STOPは非負の有限値を受理し、小数も同じ式で計算する数値的な互換拡張とします。負・非整数のpulse、壊れたJSON、必要な実時刻が表現範囲を超える入力は通常の入力不良です。未知の表示情報、BGA、scrollなどの拡張を、その存在だけで音声エラーにしません。

### 全曲位置と時刻

BPMが一定の区間では、厳密な有理数として次を計算します。STOPの値もpulse単位で同じ式へ入れます。

```text
区間tick = Δpulse × 600,000,000 / (resolution × BPM)
固定刻み寄与 = nearest-even(区間tick × 2^32)
```

制御境界までの区間寄与を一度だけ最近傍偶数丸めし、全曲の固定刻み累積値へ加えます。同位置のSTOPは有効BPMで合算した寄与を加えます。ノートと表示線は、その区間の基準時刻と局所位置差から求め、累積時計を進めません。ノートや表示線の追加・削除で、既存の発音時刻や丸め回数を変えません。初期線の判別は元pulseで行い、時刻0との比較で正pulse線を落としません。初期線0・1・2本でも通常前進とseekの小節番号は等価です。

同位置では全発音の時刻を確定してからBPM、STOPの順に作用させます。同位置BPMは配列内の最後の有効な正値を採用し、STOPはそのBPMで加算します。有限の非正値BPM変更は無視し、直前の有効BPMを維持します。無視した値をmin/maxに加えず、無視対象だけのpulseを丸め境界や譜面終端にも加えません。初期BPMの必須・正値条件とは別の規則です。変更イベントの付加的な `duration` をBPMへ推測変換しません。STOP位置の発音は停止開始時刻です。配列の記載順を時刻順と仮定せず、同位置で必要な安定順を保持します。

K回の寄与の丸めによる累積誤差上限は `K / 2^33 tick` です。局所位置差も一回丸める時刻にはその一回分を加え、二つの時刻の差には双方の上限を合成します。最終的な整数tickや音声frameの一致保証とは区別します。厳密値が量子化境界の近くにあれば整数tickで1tick、音声frameでも境界をまたぐ差が生じ得ます。表示用 `TimeSpan` への変換は末端で行い、整数tickへ落としてから音声frameを計算しません。

### 音源の再開と継続

音源チャンネル内のノートを `y` で安定ソートし、同位置を一つの組にします。いずれかのノートが `c=false` ならその組で音源を先頭へ戻します。先頭の組が `c=true` だけでも音源内開始は0です。継続では、直前の再開からの譜面上の経過時間を音源内位置へ使い、途中のSTOPも含めます。

次の異なる `y` の組が継続なら現在の論理音声区間に有限終端を設けます。次が再開または末尾ならEOFまでの尾部を残します。再開した別区間の開始だけを理由に、その尾部を打ち切りません。

同音源チャンネル・同 `y` の共有組は、論理音声区間を一つだけ作ります。全自動再生では、同じ再開から続く組を継続発音列へまとめるため、論理区間の個数とvoice数を一致させません。別音源チャンネルや別の再開列の重なりは保ち、同じpathのPCMを共有しても発音を排他にしません。異なる `y` が同じ出力frameへ丸められても、解析器の同位置の組として一律に削除しません。BMS専用の同WAV index衝突除去・次発音による打切りをbmsonへ適用しません。

`l` は音源の長さや打切りを指定しません。開始ノートの `l` から終端音を自動生成せず、明示された `up=true` の音をそのノートの `y` で発音します。孤立したupも音声から捨てません。通常の自動再生・書出しでは有効な `sound_channels` の音を対象とし、`key_channels`・`mine_channels` は鳴らしません。`mode_hint` やレーンがゲームモード外という理由だけで音声を落とさず、scrollは音声時計へ作用させません。`t`・`ln_type` は音源長や暗黙の終端音を決めません。

### 論理境界のframe変換と継続発音列

固定刻みの1秒分を `D = 10,000,000 × 2^32`、非負の固定刻み時刻をN、対象のサンプルレートをRとします。論理境界の出力frameは絶対曲時刻から `nearest-even(N × R / D)`、音源内frameは同じ再開基準からの時刻を直接 `floor(N × R / D)` で求めます。有限の論理長は変換済み終端−開始であり、丸め済み長を累積してoffsetを作りません。sourceとoutputの異なる格子でframe番号を流用しません。

全自動再生では、frame化とPCMの実EOFへの制限より前に、同じ元音源チャンネルの継続発音列を一つのvoiceへまとめます。`ResourceIndex` は元チャンネルを識別し、pathや共有PCM参照が同じという理由では別チャンネルをまとめません。連続性は `PlaybackTime` の厳密一致で判定し、前の有限 `End` と次の `Start`、前の有限 `SourceEnd` と次の `SourceStart`、両者の `Start - SourceStart` が全て一致する場合だけ連結します。

連結時は先頭の開始・音源内開始・安定順と、最後の終端を保持します。frame化で0長に見える論理区間も、その前に捨てません。前区間の `End` がnullのEOF尾部は、次の `c=false` による再開へ連結しません。別の再開は新しい独立voiceであり、前のEOF尾部と重なります。

完成した解析器の継続発音列は音源先頭から物理EOFまで再生するvoiceとなります。ネイティブの音源cursorは再開に固定し、途中の継続位置で論理境界のfloor値へ再配置しません。正しい論理source境界値と、連続再生中の物理cursorを区別します。

### 物理EOF、曲長、seek

出力開始frameをS、復号PCMの実frame数をNとすると、継続発音列の物理終端は `EndFrame = S + ceil(N × outputRate / sourceRate)` です。この終端を一度だけ解決し、通常予約、seekの有効区間判定、MusicDuration、Writerで共有します。欠落音源、正常0frame、空source窓など実出力frameのないvoiceは音声scheduleへ加えず、後方にあっても尾部を延長しません。非空voiceがなければ音声終端は0frame、MusicDurationは0です。定義なしや正常0frameの受理と全音源入力失敗を区別し、後者を空成功へ変えません。Writerの0frameエンコード拒否は[音声変換](../runtime/audio-conversion.md)に従います。正当な後方の再開や別チャンネルの非空voiceは物理EOFまで残します。物理EOFより後の継続ノートは、新しい発音や尾部、音声出力長の延長を作りません。解析器の論理終端、表示制御、ノート統計は別に保持します。

たとえば100 source framesの48kHz音源を44.1kHzへ出す一列は92 output frames、92 source framesの44.1kHz音源を48kHzへ出す一列は101 output framesです。途中の論理区間を別voiceとして再開し、93・102へ延長する規則は使いません。論理境界を物理EOFにして音源を使い切る方式を、出力終端の延長やゼロ埋めで補いません。

共有復号PCMへ独立cursorを持つ連続voiceを作り、論理区間ごとのPCM切出しコピーは行いません。seek位置Fでは全ての継続発音列の半開区間 `S <= F < EndFrame` を対象とし、source位置を `floor((F - S) × sourceRate / outputRate)` で列の再開原点から求めます。現在の論理区間の開始から求め直しません。Fより前に開始したvoiceを復元し、開始と等しいものは未来の発音として一回だけ登録します。終端と等しい列は対象にしません。

継続ノートの挿入・削除で、同じ再開列の音声や出力frame数を変えません。異rateでは同じネイティブ処理器を一つの連続sourceとして使う比較と、独立整数の終端計算を分け、別voice間のSRC位相や全プレイヤーとのPCM完全一致は保証しません。BMSでは従来の整数tick、frame丸め、次同indexによる打切り、開始＋音源全長を使う曲長・Writerの下限を維持します。

### 共通の再生経路と表示

一つの形式選択入口が、BMS解析結果の投影とbmson専用解析を同じ不変の再生データへ接続します。path、入力byte由来のhash、メタデータ、音源参照、論理音声区間、表示制御、ノート統計、表示終端を保持します。bmson用のBMS保存行、別プレイヤー、別ミキサー、別Writerは作りません。

音源の拡張子候補、入力read/decode失敗後の代替、サブフォルダーからbasenameへの救済、曲内同pathの成功・失敗共有と最終省略は[BMS共通の音源事前読込み](../runtime/audio.md#譜面の音源事前読込み)に従います。通常ロードと先読みは同じ解析・復号を使い、準備済み結果の採用で再解析・再復号しません。準備結果と資源の一回移譲、取消と解放の所有は[次曲一件の先行準備](../runtime/audio.md#次曲一件の先行準備)に従います。専用解析器が原因を保持して分類した入力不良だけを、実採用時まで保留します。OOM、DLL/ABI、native音声資源の故障、原因不明例外を入力不良や空譜面成功へ隠しません。

解析・保存の形式入口から共通譜面へ接続します。選曲・キュー・状態・表示素材は[再生パネル](../ui/playback-panel.md#共通の再生対象と一覧状態)、保存主体・操作権限は[共通モデル](chart-model.md)、変換は[音声変換](../runtime/audio-conversion.md)を正本とします。

表示統計は論理音声区間数やvoice数ではなく、採用したplayableの演奏位置を数えます。同lane・同headのnormal同士、同長LN同士は正常layerとして統計を一つにまとめ、LNは採用した開始・終端を各一つとします。別laneは独立です。`up=true` はplayable登録に入れず、LN終端の二重加算や孤立upの追加判定をしません。BGM、空打ち、地雷は通常ノートに加算しません。これはプレビューの進行表示であり、ゲームの採点値の完全再現ではありません。

### 不正LNの進行統計回復

正常な重層の意味は[bmsonのLayered Notes](https://github.com/bemusic/bmson-spec/blob/master/doc/index.rst#layered-notes)を正本とします。同 `(x,y)` の別sound channelは融合し、異なる `l` は仕様上のerrorですがwarningで扱えます。不正・曖昧入力の回復は[beatorajaの組込](https://github.com/exch-bms2/beatoraja/blob/master/build.xml)が使う[jbms-parserの固定版](https://github.com/exch-bms2/jbms-parser/blob/f32572b91b60690f6fb322156b471a290b23643e/src/bms/model/BMSONDecoder.java#L176-L301)を第一参照とし、[Bemuse](https://github.com/bemusic/bemuse/blob/master/README.md)は比較参考にします。適用するのは以下の進行統計の回復だけであり、参照プレイヤーの全挙動やゲーム音声を再現しません。

playableはsound_channelsの配列順で登録し、channel内は `y` 昇順のstable順、同 `y` は元の記載順を保持します。channelを横断する `y` 優先や最長 `l` 優先にはしません。

| 入力条件 | 進行統計の回復 |
| --- | --- |
| 同lane・同headのnormal同士、同長LN同士 | 正常layerとして一つを採用し、このWARNは出さない。 |
| 同lane・同headのnormal/LN、異長LN | 先に採用したplayableを維持し、後続を不採用とする。既存LNを延長・置換しない。 |
| 既存LNの `head < incoming y <= tail` にnormal/LNを登録 | 後続を不採用とする。既存LNのtailと同じ位置も含める。 |
| 後channelから前方LNが来て、新LNの `(head,tail]` に登録済みplayable位置がある | 新LNを不採用とし、登録済みの位置を置換しない。 |
| 別lane、up、BGM、key/mine | 別laneは独立。up/BGM等は従来どおりplayable登録・競合件数から除く。 |

不採用を適用するのはplayable進行統計だけです。全sound channelの音源参照・音声events、元channel/yの共有、`c` 再開・継続・source窓、孤立upの音、論理表示終端・制御は保持します。不採用LNのtailも表示終端へ含めます。通常Load・準備済み結果採用・Writerの音声へ統計の採否を渡しません。音を維持して進行集計を回復し、仕様違反だけで入力を拒否する条件を増やしません。

解析を正常完了した一譜面に衝突があれば、`NLogWrapper` 経由でWARNを一回出します。集約件数はplayable登録を不採用としたnormal/LNノート数です。正常layerやup/BGMを件数に含めず、衝突なしではこのWARNを出しません。解析失敗は元の失敗を保持し、競合WARNを先に出しません。事前解析でもWARNを出せます。音源入力省略の採用時WARNとUI通知の契約は変更しません。登録・範囲判定は解析中だけのlane別の順序付き集合を使い、各登録で全note/LNを走査しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 共通解析入口、BMSの五群と整数tickの維持 | [`PlaybackChart`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackChart.cs) の `Load` / `FromBms` | [`BmsonPlaybackParserTests`](../../../BeMusicSeeker.Tests/Chart/BmsonPlaybackParserTests.cs) は実 `Load` 入口を使う。[`BmsAudioFrameScheduleTests`](../../../BeMusicSeeker.Tests/Playback/BmsAudioFrameScheduleTests.cs) の既存五群・衝突・打切りと、[BMS時刻の回帰](ribbit-timing.md#実装とテストの対応)を維持する。 |
| 新旧形式・版欠落・新名優先・既定値・十進字句・入力失敗 | [`BmsonPlaybackParser`](../../../BeMusicSeeker/Ribbit/BMS/BmsonPlaybackParser.cs) | `BmsonPlaybackParserTests` の `LegacyAndVersionlessAdaptersShareModernTimingAndModernKeysWin`、`ResolutionDefaultsAndNumericStringsKeepQuarterTime`、`ResolutionCanBeDoubledOrAbsoluteMinimumIntegerWithoutWrapping`、`DecimalLexemesAndExponentKeepExactTimeAndIgnoreAdditionalNotes`、初期BPMの失敗ケースが独立入力を検査する。 |
| 十進字句の実用上限と元原因、scale・resolution相殺、指数先頭0 | 同解析器の `Number` | `BmsonPlaybackParserTests.DecimalCoefficientLimitCountsZerosButExcludesSignAndPoint` / `ExplicitExponentLimitAllowsScaleAndResolutionToCancelBeyondEffectiveRange` は数値・数値文字列を同条件で確認する。既存 `FiniteDecimalOutsideDoubleRangeIsNotRejectedAndMissingBpmIsNotInvented` と実時刻溢れ検査を維持する。巨大整数化前の検査・0のPow省略は静的確認する。 |
| 全曲pulse、表示線独立、同位置BPM・STOP、小数STOP、非正BPM変更の無視 | 同解析器の制御境界と `TimeAt` | 同テストの `GlobalPulseAndDisplayLinesDoNotChangeAudioClock`、`SamePulseSoundsBeforeLastBpmAndSummedStops`、`FractionalStopsAndNumericStringStopsHaveExactContributions`。`IgnoredNonpositiveBpmChangesDoNotCreateAnchorsExtendDurationOrReplaceLastValidBpm` は時計・音声区間・曲長・min/maxの維持と同位置の最後の有効BPMを確認する。 |
| 固定刻み、直接の出力最近傍偶数・source floor、BMS整数tick | [`PlaybackTime`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackTime.cs) | `BmsonPlaybackParserTests.FixedSubtickContributionsRoundTiesToEven` / `MultipleDecimalControlContributionsStayWithinTheFixedGridErrorBound`、[`AudioFrameMathTests`](../../../BeMusicSeeker.Tests/Playback/AudioFrameMathTests.cs) の `SubtickFrameBoundariesAreDirectAndBmsTicksRemainExact` が独立整数比と半frame境界を確認する。 |
| 同位置共有、再開・継続・STOP込みoffset、LN・releaseと表示統計 | `BmsonPlaybackParser` の音声区間・判定位置生成 | `BmsonPlaybackParserTests` の `ContinuationsUseRestartWallTimeAndEofTailsOverlap`、`SameChannelPulseIsOneSliceAndAnyRestartWinsRegardlessOfNoteOrder`、`LayeredLongNoteHeadsAndReleaseSoundsUseSeparateAudioAndCountIdentities`。 |
| 不正LNの登録優先・同head競合・内側/尾端・後channelの前方LN、前進と戻りseek | 同解析器の進行登録、`BMSPlayer` の共通進行 | [`BMSPlayerControlTests`](../../../BeMusicSeeker.Tests/Playback/BMSPlayerControlTests.cs) の `BmsonPlayableConflictsPreserveRegistrationOrderForProgressAndBackwardSeek` は125 BPM/240 resolutionの独立時刻で、normal/LNと異長LNの順序4ケース、stable登録と別lane、後channelの前方LNを公開TotalNoteCount/CurrentTime/Comboで確認する。native音声は使わない。 |
| 競合の集約WARN一回・件数6、正常layerと解析失敗でWARNなし | 同解析器の正常完了時ログ | `BmsonPlaybackParserTests.ConflictingPlayableNotesWarnOnceWithRejectedCountButNormalLayersAndFailedParseDoNot` は実wrapperとMemoryTargetを使い、methodだけを直列化する。正常normal/LN重層は既存 `LayeredLongNoteHeadsAndReleaseSoundsUseSeparateAudioAndCountIdentities` に統合する。 |
| 不採用playableの音声区間・source窓・音源参照・表示終端保持 | 同解析器の音声groupと進行統計の分離 | `BmsonPlaybackParserTests.RejectedPlayableNotesKeepEveryAudioSliceSourceWindowAndLogicalDisplayEnd` はlaneだけ異なる非衝突入力と全音声events・制御を比較し、独立した6events、再開/継続/孤立upと不採用LN由来の3.84秒終端を確認する。既存音声・Writer・seek回帰を維持する。 |
| 論理境界の維持、量子化前の継続列連結、物理EOF、別の再開・チャンネルの独立性 | [`PlaybackChart`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackChart.cs) の論理区間、[`BmsAudioFrameSchedule`](../../../BeMusicSeeker/Ribbit/BMS/BmsAudioFrameSchedule.cs) の共通 `Create` / `CoalesceContinuationRuns` | `BmsonPlaybackParserTests` は論理時刻・source floorを、[`BmsAudioFrameScheduleTests`](../../../BeMusicSeeker.Tests/Playback/BmsAudioFrameScheduleTests.cs) の `BmsonContinuationRunsKeepIndependentChannelsAtCollidingOutputFrames` / `BmsonContinuationRunResolvesPhysicalEofWithoutExtendingAtLaterLogicalNotes` は量子化前の連結、独立チャンネル、44.1/48kHz両方向の実EOFを確認する。 |
| 同pathのPCM共有、採用時の再解析・再復号なし | [`BmsAudioResourceLoader`](../../../BeMusicSeeker/Ribbit/BMS/BmsAudioResourceLoader.cs)、[`PreparedBmsSong`](../../../BeMusicSeeker/Ribbit/BMS/PreparedBmsSong.cs)、[`BMSAutoPlayer`](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayer.cs) | [`BMSAutoPlayerInputTests`](../../../BeMusicSeeker.Tests/Playback/BMSAutoPlayerInputTests.cs) の `BmsonPreparationKeepsSharedPcmAndParsedSlicesAfterInputsDisappear` と `NextSongFallbackPreparationAdoptsDecodedResultAfterInputsDisappear` は代替成功を含む入力消失後も同じ解析結果・PCMを採用する。実decoderとBASS/NLogを使う検査は既存の直列群で行う。 |
| 重なる継続発音列のseek、再開原点のsource cursor、半開終端 | [`BmsScheduledAudioMixer`](../../../BeMusicSeeker/Ribbit/BMS/BmsScheduledAudioMixer.cs)、[`BmsRealtimeAudioScheduler`](../../../BeMusicSeeker/Ribbit/BMS/BmsRealtimeAudioScheduler.cs) | [`BmsRealtimeAudioSchedulerTests`](../../../BeMusicSeeker.Tests/Playback/BmsRealtimeAudioSchedulerTests.cs) の `BmsonSeekRestoresEveryOverlappingRestartRunAtItsSourceOffset` / `BmsonSeekRestoresBothRestartRunsFromTheirOwnOrigins` は別の再開列の重なりを、`BmsonSeekAfterContinuationUsesTheRunOriginAndHalfOpenPhysicalEof` は継続位置を越えたseekで再開原点から求めるsource cursorと半開終端を確認する。 |
| 継続ノート追加・削除でPCMと長さ不変、別チャンネル・再開EOF尾部の重なり、LNと明示up | [`BMSAutoPlayWriter`](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayWriter.cs)、共通mixer | [`BMSAutoPlayWriterTests`](../../../BeMusicSeeker.Tests/Playback/BMSAutoPlayWriterTests.cs) の `BmsonWriterContinuationInsertionKeepsContinuousPcmAndPhysicalEof` は同rate・異rateの一つの連続sourceとの対を、`BmsonWriterContinuationRunsPreserveRestartAndSamePathChannelOverlap` は独立した再開・別チャンネルの重なりを、`BmsonWriterLongNoteDoesNotCutAudioAndExplicitOrphanUpSoundsAtItsOwnPulse` はLNと明示upを出力WAVの実frame・PCMで確認する。既存BMSの長さ・末尾無音の検査を維持する。 |
| 先読みの形式選択、一回移譲、入力失敗保留・fatalの所有 | [`NextSongPreloadOwner`](../../../BeMusicSeeker/Models/Playback/NextSongPreloadOwner.cs)、[`InternalBMSAutoPlayerSoundOnly`](../../../BeMusicSeeker/Models/Playback/InternalBMSAutoPlayerSoundOnly.cs) | [`NextSongPreloadOwnerTests`](../../../BeMusicSeeker.Tests/Playback/NextSongPreloadOwnerTests.cs) の `BmsonPreloadDispatchesModernOrLegacyAndKeepsInputFailureUntilAdoption` と既存取消・fatal・一回移譲の検査。 |
| 形式入口・外部player能力・保存権限の境界 | 共通モデルへの投影、外部player選択、操作能力 | [再生パネルの外部能力検査](../ui/playback-panel.md#実装とテストの対応)、[共通モデルの能力・メニューテスト](chart-model.md#実装とテストの対応)、[音声変換の選択入口検査](../runtime/audio-conversion.md#実装とテストの対応)を参照する。 |
| 非空voiceだけの音声終端、通常ロード・準備結果採用・Writerの一致 | `BmsAudioFrameSchedule.Create`、`BMSAutoPlayer`、`BMSAutoPlayWriter` | `BMSAutoPlayWriterTests.BmsonAudioEndUsesOnlyNonemptyVoicesAcrossLoadPreparationAndWriter` は後方欠落・正常0frame・空source窓で480frame/10ms、後方再開・別チャンネルで24480frame/510ms、非空voiceなしで0frame/0msを確認し、表示終端・ノート情報と既存BMSの長さ検査を維持する。 |
| 初期線保持・小節数・量子化された正線・音声時計不変 | `BmsonPlaybackParser` / `PlaybackControlKind.InitialBarLine` / `BMSPlayer` | [`BmsonPlaybackParserTests`](../../../BeMusicSeeker.Tests/Chart/BmsonPlaybackParserTests.cs) の `GlobalPulseAndDisplayLinesDoNotChangeAudioClock` は初期線0・1・2本、LastMeasureと全音声区間を確認する。[`BMSPlayerControlTests`](../../../BeMusicSeeker.Tests/Playback/BMSPlayerControlTests.cs) の `BmsonInitialLinesDoNotAdvanceMeasureButQuantizedPositiveLinesDo` は実Start前進と公開CurrentTimeの戻りseekを分け、通常BPMと巨大BPMを確認する。 |

### 実譜面確認の範囲

音源なしで実DLLの再生用解析器を呼ぶ開発確認では、TestDataの1,045件すべてが解析と音声区間計画を完了しています。条件・入力識別・解析全体の性能は[設計判断](../../decisions/bmson-playback-boundaries.md#再生用解析器全体の実譜面確認)に記載します。全件の時刻を別の厳密時計と照合した検査ではありません。

このLinuxでの確認は、音源復号、ネイティブミキサー・Writer・シーク、WPF、混在キュー、資源寿命の実行確認を含みません。上表のテストの責務と、その測定で実際に実行した範囲を区別します。Windowsでの音声・画面検証は[テスト検証](../development/testing.md)の実行条件に従います。

## 関連資料

- [bmson再生の設計判断](../../decisions/bmson-playback-boundaries.md)
- [BMSの時刻計算](ribbit-timing.md)
- [音声実行基盤](../runtime/audio.md)・[音声変換](../runtime/audio-conversion.md)
- [bmson 1.0.0仕様](https://github.com/bemusic/bmson-spec/blob/master/doc/index.rst)
