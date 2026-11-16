# bmsonの内蔵再生と音声書出し

## 目的と適用範囲

bmsonの音声を、BMSと共通の事前復号、発音の予約、内蔵プレイヤー、先読み、音声書出しへ接続する契約です。1.0.0と旧0.21を対象とし、通常のbeatoraja・Bemuse向けの譜面の音声プレビューに必要な互換性を扱います。特定のプレイヤーの採点・不正入力の処理・PCMの完全な再現と、BGAの表示は保証しません。

`chart_info` 用の解析とは別の経路です。再生用の解析の受理条件や時刻を、[譜面情報用の解析器](parser-compatibility.md)とDBの保存値へ適用しません。BMSの[時刻計算](ribbit-timing.md)と[発音規則](../runtime/audio.md#bms音声のframe予約)は維持します。採用理由と実譜面の測定は[設計判断](../../decisions/bmson-playback-boundaries.md)に分けます。

## 用語

共通の語義は[用語集](../glossary.md)に従います。機能固有の用語は次のとおりです。

| 用語 | 意味 |
| --- | --- |
| pulse | 曲の先頭を0とする整数の位置。`resolution` は四分音符当たりのpulse数。小節線で位置をリセットしない。 |
| 固定刻み時刻 | 100nsの1tickを `2^32` に分割した刻みの整数値。曲全体の累積値を `BigInteger` で保持する。 |
| 制御境界 | BPMの変更またはSTOPの位置。時刻計算の区間を分ける。 |
| 同位置の組 | 一つの音源チャンネルの中で `y` が等しいノートの集合。音源の再開・継続を一回で解決する。 |
| 論理音声区間 | 解析器が同位置の組ごとに作る、曲内の開始、音源内の開始、有限の終端またはEOF。判定の位置や音の打切りの情報であり、一件ずつネイティブの発音を作る指示ではない。 |
| 継続発音列 | 元の音源チャンネルの一回の再開と、そこから続く `c=true` の組。全自動再生では一つの連続した発音として出力する。 |
| 発音 | 音声の予約・ミキサーが扱う、一つの音源の連続した再生単位。コード上の voice。 |
| 演奏ノート | 進行の統計に登録する、レーンを持つ通常ノート・LN。コード上の playable。 |

## 仕様

### 形式の選択と入力の正規化

明示した `version="1.0.0"` は新形式、`version="0.21"` は旧形式として読みます。版が欠けている場合は、旧名があれば旧形式を選び、それ以外の既知の新形式は新形式として読みます。未知の版でも、既知の新形式の構造を読めればそのフィールドを処理しますが、将来の版全体への対応は保証しません。明示した `version=null` は入力エラーです。

旧形式の `soundChannel`、`info.initBPM`、`bpmNotes[].v`、`stopNotes[].v` を、共通の入力へ正規化します。旧形式の `resolution` は240に固定します。同じ意味の新旧の名前が混在した場合は、新しい名前があればその値を優先します。新しい名前の値がnullでも旧名へ戻さず、そのフィールドのnullの規則を使います。配列を連結して発音を二重にすることはしません。

| 入力 | 既定値・条件 |
| --- | --- |
| 新形式の `resolution` | 欠落・null・0は240。負の値は整数として絶対値を取り、固定幅の符号反転であふれさせない。 |
| 初期BPM | 正の有限値が必須。欠落・正でない値・有限でない値を120などで補わない。 |
| 表示文字列・`subartists` | 欠落・nullは空文字列・空配列。 |
| 新形式の `total` / `mode_hint` | 既定は100 / `beat-7k`。旧形式のtotalは新形式への比率換算をせず、表示値として保持する。 |
| 任意のイベント配列・音源チャンネルの `notes` | 欠落・nullは空配列。 |
| `lines` | 欠落・nullは四拍ごとの表示線、明示した空配列は線なし。明示した `y=0` は初期線として重複も保持し、CurrentMeasure・LastMeasureへ加算しない。正のpulseの線は、時刻0へ量子化されても加算する。どの線も音声の時計を変えない。 |
| ノートの `x` / `l` / `c` / `up` | `x` の欠落・null・0はBGM、`l` の欠落は0、`c`・`up` の欠落はfalse。`y`・`l` は負でない整数。 |

#### 十進数の字句

JSONの有限の十進値は、数値字句の係数と指数から厳密な有理数へ変換します。有効な有限のJSON数値の字句を持つ数値文字列も受理します。二進の浮動小数へ変換した値を、計算や比較の入力に戻しません。

正の有限十進値は、`double` の範囲を超えるという理由だけでは拒否しません。たとえば `1e1000` は受理しますが、範囲内でも必要な実際の時刻が表現範囲を超えれば失敗します。

字句には次の上限を設け、`BigInteger` の係数やべき乗を作る前に検査します。数値と数値文字列は同じ条件です。

| 対象 | 上限と数え方 |
| --- | --- |
| 仮数の全数字 | 最大4096桁。符号と小数点は数えず、0と末尾の0は数える。字句全体の長さには別の上限を設けない。 |
| 明示した十進指数 | `[-4096,4096]`。指数の先頭の0は数値として扱う。位取りを差し引いた実効の指数には、別の±4096の上限を置かない。 |
| 係数が0の値 | 先に上の上限を検査し、その後はべき乗を作らず0とする（`0e5000` は拒否する）。 |

上限を超えた場合は、元の原因を付けた `InvalidBmsonFileException` とし、丸めや既定値への変換はしません。この上限は、`Fraction`、BMSの数値、固定刻みの時計、DB用の `chart_info` の受理条件には及ぼしません。

STOPは負でない有限値を受理し、小数も同じ式で計算する数値上の互換の拡張とします。負・整数でないpulse、壊れたJSON、必要な実際の時刻が表現範囲を超える入力は、通常の入力不良です。未知の表示情報、BGA、scrollなどの拡張を、その存在だけで音声のエラーにしません。

### 全曲位置と時刻

BPMが一定の区間では、厳密な有理数として次を計算します。STOPの値もpulse単位で同じ式に入れます。

```text
区間tick = Δpulse × 600,000,000 / (resolution × BPM)
固定刻み寄与 = nearest-even(区間tick × 2^32)
```

制御境界までの区間の寄与を一度だけ最近接偶数へ丸め、全曲の固定刻みの累積値へ加えます。同位置のSTOPは、有効なBPMで合算した寄与を加えます。ノートと表示線は、その区間の基準時刻と局所的な位置の差から求め、累積の時計を進めません。ノートや表示線の追加・削除で、既存の発音時刻や丸めの回数を変えません。初期線の判別は元のpulseで行い、時刻0との比較で正のpulseの線を落としません。初期線が0・1・2本のどれでも、通常の前進とシークでの小節番号は等価です。

同位置では、全発音の時刻を確定してからBPM、STOPの順に作用させます。同位置のBPMは配列内の最後の有効な正の値を採用し、STOPはそのBPMで加算します。有限で正でないBPMの変更は無視し、直前の有効なBPMを維持します。無視した値は最小・最大に加えず、無視した変更だけがあるpulseを丸めの境界や譜面の終端にも加えません。これは初期BPMの必須・正の値の条件とは別の規則です。変更イベントに付いた `duration` をBPMへ推測で変換しません。STOPの位置の発音は、停止の開始時刻に鳴らします。配列の記載順を時刻順と仮定せず、同位置で必要な安定した順序を保持します。

K回の寄与の丸めによる累積誤差の上限は `K / 2^33 tick` です。局所的な位置の差を一回丸める時刻にはその一回分を加え、二つの時刻の差には双方の上限を合わせます。これは最終的な整数tickや音声フレームの一致の保証とは別です。厳密な値が量子化の境界の近くにあれば、整数tickで1tick、音声フレームでも境界をまたぐ差が生じ得ます。表示用の `TimeSpan` への変換は末端で行い、整数tickへ落としてから音声フレームを計算しません。

### 音源の再開と継続

音源チャンネル内のノートを `y` で安定に整列し、同位置のものを一つの組にします。組の中にいずれか `c=false` のノートがあれば、その組で音源を先頭へ戻します。先頭の組が `c=true` だけでも、音源内の開始は0です。継続では、直前の再開からの譜面上の経過時間を音源内の位置に使い、途中のSTOPも含めます。

次の異なる `y` の組が継続なら、現在の論理音声区間に有限の終端を設けます。次が再開または末尾なら、EOFまでの尾部を残します。再開した別の区間が始まることだけを理由に、その尾部を打ち切りません。

同じ音源チャンネル・同じ `y` の組は、論理音声区間を一つだけ作ります。全自動再生では、同じ再開から続く組を継続発音列へまとめるため、論理区間の個数と発音の数は一致しません。別の音源チャンネルや別の再開の列の重なりは保ち、同じパスのPCMを共有していても発音を排他にしません。異なる `y` が同じ出力フレームへ丸められても、解析器の同位置の組として一律に削除しません。BMS専用の、同じWAV番号の衝突の除去や、次の発音による打切りをbmsonへ適用しません。

`l` は音源の長さや打切りを指定しません。開始ノートの `l` から終端の音を自動で作らず、明示した `up=true` の音をそのノートの `y` で鳴らします。孤立したupも音声から捨てません。通常の自動再生・書出しでは有効な `sound_channels` の音を対象とし、`key_channels`・`mine_channels` は鳴らしません。`mode_hint` やレーンがゲームモードの外にあるという理由だけで音声を落とさず、scrollは音声の時計へ作用させません。`t`・`ln_type` は、音源の長さや暗黙の終端の音を決めません。

### 論理境界のframe変換と継続発音列

固定刻みの1秒分を `D = 10,000,000 × 2^32`、負でない固定刻み時刻をN、対象のサンプルレートをRとします。論理境界の出力フレームは、曲の絶対時刻から `nearest-even(N × R / D)` で求めます。音源内のフレームは、同じ再開を基準とする時刻から直接 `floor(N × R / D)` で求めます。有限の論理長は変換済みの終端から開始を引いた値であり、丸め済みの長さを累積してオフセットを作りません。音源と出力の異なる格子の間で、フレーム番号を流用しません。

全自動再生では、フレーム化と、PCMの実際のEOFへの制限より前に、同じ元の音源チャンネルの継続発音列を一つの発音へまとめます。`ResourceIndex` は元のチャンネルを識別し、パスや共有するPCMの参照が同じという理由では、別のチャンネルをまとめません。連続性は `PlaybackTime` の厳密な一致で判定し、次の三つがすべて一致する場合だけ連結します。

- 前の有限の `End` と、次の `Start`
- 前の有限の `SourceEnd` と、次の `SourceStart`
- 両者の `Start - SourceStart`

連結したときは、先頭の開始・音源内の開始・安定した順序と、最後の終端を保持します。フレーム化で長さ0に見える論理区間も、その前の段階で捨てません。前の区間の `End` がnullのEOFの尾部は、次の `c=false` による再開へ連結しません。別の再開は新しい独立した発音であり、前のEOFの尾部と重なります。

解析器が作った継続発音列は、音源の先頭から物理的なEOFまで再生する発音になります。ネイティブの音源の読取り位置は再開の位置に固定し、途中の継続の位置で論理境界のfloor値へ置き直しません。正しい論理的な音源の境界値と、連続再生中の物理的な読取り位置を区別します。

### 物理EOF、曲長、seek

出力の開始フレームをS、復号したPCMの実際のフレーム数をNとすると、継続発音列の物理的な終端は `EndFrame = S + ceil(N × outputRate / sourceRate)` です。この終端を一度だけ求め、通常の予約、シークでの有効区間の判定、MusicDuration、Writerで共有します。

| 発音 | 扱い |
| --- | --- |
| 欠落した音源、正常な0フレーム、空の音源窓など、実際の出力フレームがない発音 | 音声の予定へ加えず、後方にあっても尾部を延長しない。 |
| 正当な後方の再開や、別チャンネルの空でない発音 | 物理的なEOFまで残す。 |
| 物理的なEOFより後の継続ノート | 新しい発音・尾部・音声の出力長の延長を作らない。 |

空でない発音がなければ、音声の終端は0フレーム、MusicDurationは0です。定義がない・正常な0フレームの受理と、全音源の入力失敗を区別し、後者を空の成功に変えません。Writerが0フレームのエンコードを拒否する扱いは[音声変換](../runtime/audio-conversion.md)に従います。解析器の論理的な終端、表示の制御、ノートの統計は別に保持します。

たとえば、100 source frames の48kHzの音源を44.1kHzへ出す一列は92 output frames、92 source frames の44.1kHzの音源を48kHzへ出す一列は101 output frames です。途中の論理区間を別の発音として再開し、93・102へ延長する規則は使いません。論理境界を物理的なEOFとして音源を使い切る方式を、出力の終端の延長やゼロ埋めで補いません。

共有した復号済みPCMに、それぞれ独立した読取り位置を持つ連続した発音を作り、論理区間ごとにPCMを切り出してコピーすることはしません。シーク位置Fでは、全継続発音列の半開区間 `S <= F < EndFrame` を対象とし、音源の位置を `floor((F - S) × sourceRate / outputRate)` で列の再開の原点から求めます。現在の論理区間の開始から求め直しません。Fより前に開始した発音を復元し、開始がFと等しいものは将来の発音として一回だけ登録します。終端がFと等しい列は対象にしません。

継続ノートの挿入・削除で、同じ再開の列の音声や出力フレーム数を変えません。サンプルレートが異なる場合の検証では、同じネイティブ処理器を一つの連続した音源として使う比較と、独立した整数での終端の計算を分けます。別の発音の間のサンプルレート変換の位相や、全プレイヤーとのPCMの完全な一致は保証しません。BMSでは、整数tick、フレームの丸め、次の同じ番号による打切り、開始と音源の全長を使う曲長・Writerの下限を維持します。

### 共通の再生経路と表示

一つの形式選択の入口が、BMSの解析結果の投影とbmson専用の解析を、同じ不変の再生データへ接続します。再生データは、パス、入力のバイト列から求めたハッシュ、メタデータ、音源の参照、論理音声区間、表示の制御、ノートの統計、表示の終端を保持します。bmson用のBMS保存行、別のプレイヤー、別のミキサー、別のWriterは作りません。

音源の拡張子の候補、入力の読取り・復号の失敗後の代替、サブフォルダーからファイル名だけでの救済、曲内で同じパスの成功・失敗の共有と最終的な省略は、[BMS共通の音源の事前読込み](../runtime/audio.md#譜面の音源事前読込み)に従います。通常のロードと先読みは同じ解析・復号を使い、準備済みの結果を採用するときに再解析・再復号しません。準備結果と資源の一回の移譲、取消と解放の所有は[次曲一件の先行準備](../runtime/audio.md#次曲一件の先行準備)に従います。専用の解析器が原因を保持して分類した入力不良だけを、実際に採用するときまで保留します。OOM、DLL・ABI、ネイティブの音声資源の故障、原因不明の例外を、入力不良や空の譜面の成功へ隠しません。

選曲・キュー・状態・表示素材は[再生パネル](../ui/playback-panel.md#共通の再生対象と一覧状態)、保存行・操作権限は[共通モデル](chart-model.md)、変換は[音声変換](../runtime/audio-conversion.md)を正本とします。

表示の統計は、論理音声区間の数や発音の数ではなく、採用した演奏ノートの演奏位置を数えます。同じレーン・同じ始点の通常ノートどうしと、同じ長さのLNどうしは正常な重層として一つにまとめ、LNは採用した開始・終端を各一つとします。別のレーンは独立です。`up=true` は演奏ノートとして登録せず、LNの終端の二重加算や、孤立したupの追加の判定をしません。BGM、空打ち、地雷は通常ノートに加算しません。これはプレビューの進行表示であり、ゲームの採点値の完全な再現ではありません。

### 不正LNの進行統計回復

正常な重層の意味は[bmsonのLayered Notes](https://github.com/bemusic/bmson-spec/blob/master/doc/index.rst#layered-notes)を正本とします。同じ `(x,y)` の別のsound channelは融合し、異なる `l` は仕様上はエラーですが警告として扱えます。不正・曖昧な入力の回復は、[beatorajaの組込み](https://github.com/exch-bms2/beatoraja/blob/master/build.xml)が使う[jbms-parserの固定版](https://github.com/exch-bms2/jbms-parser/blob/f32572b91b60690f6fb322156b471a290b23643e/src/bms/model/BMSONDecoder.java#L176-L301)を第一の参照とし、[Bemuse](https://github.com/bemusic/bemuse/blob/master/README.md)は比較の参考にします。適用するのは次の進行統計の回復だけであり、参照プレイヤーの全挙動やゲームの音声は再現しません。

演奏ノートはsound_channelsの配列順に登録し、チャンネルの中は `y` の昇順の安定した順、同じ `y` は元の記載順を保持します。チャンネルを横断する `y` の優先や、最長の `l` の優先にはしません。登録と範囲の判定では、登録のたびに全ノート・LNを走査しません。

| 入力の条件 | 進行統計の回復 |
| --- | --- |
| 同じレーン・同じ始点の通常ノートどうし、同じ長さのLNどうし | 正常な重層として一つを採用し、この警告ログは出さない。 |
| 同じレーン・同じ始点の通常ノートとLN、長さの異なるLN | 先に採用した演奏ノートを維持し、後続を採用しない。既存のLNを延長・置換しない。 |
| 既存のLNの `始点 < 後続のy <= 終点` に通常ノート・LNを登録する | 後続を採用しない。既存のLNの終点と同じ位置も含める。 |
| 後のチャンネルから前方のLNが来て、新しいLNの `(始点,終点]` に登録済みの演奏位置がある | 新しいLNを採用せず、登録済みの位置を置き換えない。 |
| 別のレーン、up、BGM、key・mine | 別のレーンは独立。up・BGMなどは演奏ノートの登録と競合の件数から除く。 |

不採用を適用するのは演奏ノートの進行統計だけです。全sound channelの音源の参照と音声のイベント、元のチャンネル・yの共有、`c` による再開・継続・音源窓、孤立したupの音、論理的な表示の終端・制御は保持します。不採用のLNの終点も表示の終端に含めます。通常のロード・準備済みの結果の採用・Writerの音声へ、統計の採否を渡しません。音を維持して進行の集計を回復し、仕様違反だけを理由に入力を拒否する条件は増やしません。

解析を正常に完了した一譜面に衝突があれば、`NLogWrapper` 経由で警告ログ（WARN）を一回出します。集約する件数は、演奏ノートとして登録しなかった通常ノート・LNの数です。正常な重層やup・BGMを件数に含めず、衝突がなければこの警告ログは出しません。解析が失敗した場合は元の失敗を保持し、競合の警告ログを先に出しません。事前解析でも警告ログを出せます。音源の入力を省略したときの採用時の警告ログとUIの通知の契約は変えません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 共通の解析入口、BMSの五群と整数tickの維持 | [`PlaybackChart`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackChart.cs) の `Load` / `FromBms` | [`BmsonPlaybackParserTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Chart/BmsonPlaybackParserTests.cs) / [`BmsonPlaybackParserTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Chart/BmsonPlaybackParserTests.cs) は実際の `Load` 入口を使う。[`BmsAudioFrameScheduleTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/BmsAudioFrameScheduleTests.cs) の既存の五群・衝突・打切りと、[BMS時刻の回帰検証](ribbit-timing.md#実装とテストの対応)を維持する。 |
| 新旧の形式・版の欠落・新しい名前の優先・既定値・十進の字句・入力の失敗 | [`BmsonPlaybackParser`](../../../BeMusicSeeker/Ribbit/BMS/BmsonPlaybackParser.cs) | `BmsonPlaybackParserTests` の `LegacyAndVersionlessAdaptersShareModernTimingAndModernKeysWin`、`ResolutionDefaultsAndNumericStringsKeepQuarterTime`、`ResolutionCanBeDoubledOrAbsoluteMinimumIntegerWithoutWrapping`、`DecimalLexemesAndExponentKeepExactTimeAndIgnoreAdditionalNotes` と、初期BPMの失敗のケースが、独立した入力を検査する。 |
| 十進の字句の上限と元の原因、位取り・resolutionの相殺、指数の先頭の0 | 同じ解析器の `Number` | `BmsonPlaybackParserTests.DecimalCoefficientLimitCountsZerosButExcludesSignAndPoint` / `ExplicitExponentLimitAllowsScaleAndResolutionToCancelBeyondEffectiveRange` は、数値と数値文字列を同じ条件で確認する。既存の `FiniteDecimalOutsideDoubleRangeIsNotRejectedAndMissingBpmIsNotInvented` と、実際の時刻のあふれの検査を維持する。巨大な整数にする前の検査と、0でのべき乗の省略は静的に確認する。 |
| 全曲のpulse、表示線からの独立、同位置のBPM・STOP、小数のSTOP、正でないBPM変更の無視 | 同じ解析器の制御境界と `TimeAt` | 同じテストの `GlobalPulseAndDisplayLinesDoNotChangeAudioClock`、`SamePulseSoundsBeforeLastBpmAndSummedStops`、`FractionalStopsAndNumericStringStopsHaveExactContributions`。`IgnoredNonpositiveBpmChangesDoNotCreateAnchorsExtendDurationOrReplaceLastValidBpm` は、時計・音声区間・曲長・最小・最大の維持と、同位置の最後の有効なBPMを確認する。 |
| 固定刻み、出力の最近接偶数・音源のfloorの直接計算、BMSの整数tick | [`PlaybackTime`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackTime.cs) | `BmsonPlaybackParserTests.FixedSubtickContributionsRoundTiesToEven` / `MultipleDecimalControlContributionsStayWithinTheFixedGridErrorBound`、[`AudioFrameMathTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/AudioFrameMathTests.cs) の `SubtickFrameBoundariesAreDirectAndBmsTicksRemainExact` が、独立した整数比と半フレームの境界を確認する。 |
| 同位置の共有、再開・継続・STOPを含むオフセット、LN・releaseと表示の統計 | `BmsonPlaybackParser` の音声区間・判定位置の生成 | `BmsonPlaybackParserTests` の `ContinuationsUseRestartWallTimeAndEofTailsOverlap`、`SameChannelPulseIsOneSliceAndAnyRestartWinsRegardlessOfNoteOrder`、`LayeredLongNoteHeadsAndReleaseSoundsUseSeparateAudioAndCountIdentities`。 |
| 不正なLNの登録の優先・同じ始点の競合・内側と終点・後のチャンネルの前方のLN、前進と戻りのシーク | 同じ解析器の進行の登録、`BMSPlayer` の共通の進行 | [`BMSPlayerControlTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/BMSPlayerControlTests.cs) の `BmsonPlayableConflictsPreserveRegistrationOrderForProgressAndBackwardSeek` は、125 BPM・resolution 240の独立した時刻で、通常ノートとLN・長さの異なるLNの順序の4ケース、安定した登録と別のレーン、後のチャンネルの前方のLNを、公開する TotalNoteCount・CurrentTime・Combo で確認する。ネイティブの音声は使わない。 |
| 競合の集約した警告ログの一回・件数6、正常な重層と解析失敗で警告ログなし | 同じ解析器の正常完了時のログ | `BmsonPlaybackParserTests.ConflictingPlayableNotesWarnOnceWithRejectedCountButNormalLayersAndFailedParseDoNot` は、実際のログの窓口とメモリ上の出力先を使い、このメソッドだけを直列化する。正常な通常ノート・LNの重層は、既存の `LayeredLongNoteHeadsAndReleaseSoundsUseSeparateAudioAndCountIdentities` に統合する。 |
| 不採用の演奏ノートでの音声区間・音源窓・音源の参照・表示の終端の保持 | 同じ解析器の音声のグループと進行統計の分離 | `BmsonPlaybackParserTests.RejectedPlayableNotesKeepEveryAudioSliceSourceWindowAndLogicalDisplayEnd` は、レーンだけが異なる衝突しない入力と全音声のイベント・制御を比較し、独立した6イベント、再開・継続・孤立したupと、不採用のLNに由来する3.84秒の終端を確認する。既存の音声・Writer・シークの回帰検証を維持する。 |
| 論理境界の維持、量子化前の継続列の連結、物理的なEOF、別の再開・チャンネルの独立 | [`PlaybackChart`](../../../BeMusicSeeker/Ribbit/BMS/PlaybackChart.cs) の論理区間、[`BmsAudioFrameSchedule`](../../../BeMusicSeeker/Ribbit/BMS/BmsAudioFrameSchedule.cs) の共通の `Create` / `CoalesceContinuationRuns` | `BmsonPlaybackParserTests` が論理時刻・音源のfloorを、[`BmsAudioFrameScheduleTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/BmsAudioFrameScheduleTests.cs) の `BmsonContinuationRunsKeepIndependentChannelsAtCollidingOutputFrames` / `BmsonContinuationRunResolvesPhysicalEofWithoutExtendingAtLaterLogicalNotes` が、量子化前の連結、独立したチャンネル、44.1kHz・48kHzの両方向の実際のEOFを確認する。 |
| 同じパスのPCMの共有、採用時に再解析・再復号しないこと | [`BmsAudioResourceLoader`](../../../BeMusicSeeker/Ribbit/BMS/BmsAudioResourceLoader.cs)、[`PreparedBmsSong`](../../../BeMusicSeeker/Ribbit/BMS/PreparedBmsSong.cs)、[`BMSAutoPlayer`](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayer.cs) | [`BMSAutoPlayerInputTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BMSAutoPlayerInputTests.cs) の `BmsonPreparationKeepsSharedPcmAndParsedSlicesAfterInputsDisappear` と `NextSongFallbackPreparationAdoptsDecodedResultAfterInputsDisappear` が、代替の成功を含め、入力が消えた後も同じ解析結果・PCMを採用することを確認する。実際の復号器とBASS・NLogを使う検査は、既存の直列のテスト群で行う。 |
| 重なる継続発音列のシーク、再開の原点からの音源の読取り位置、半開の終端 | [`BmsScheduledAudioMixer`](../../../BeMusicSeeker/Ribbit/BMS/BmsScheduledAudioMixer.cs)、[`BmsRealtimeAudioScheduler`](../../../BeMusicSeeker/Ribbit/BMS/BmsRealtimeAudioScheduler.cs) | [`BmsRealtimeAudioSchedulerTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BmsRealtimeAudioSchedulerTests.cs) の `BmsonSeekRestoresEveryOverlappingRestartRunAtItsSourceOffset` / `BmsonSeekRestoresBothRestartRunsFromTheirOwnOrigins` が別の再開の列の重なりを、`BmsonSeekAfterContinuationUsesTheRunOriginAndHalfOpenPhysicalEof` が継続の位置を越えたシークでの、再開の原点から求める読取り位置と半開の終端を確認する。 |
| 継続ノートの追加・削除でPCMと長さが変わらないこと、別のチャンネル・再開のEOFの尾部の重なり、LNと明示したup | [`BMSAutoPlayWriter`](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayWriter.cs)、共通のミキサー | [`BMSAutoPlayWriterTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BMSAutoPlayWriterTests.cs) の `BmsonWriterContinuationInsertionKeepsContinuousPcmAndPhysicalEof` は同じ・異なるサンプルレートでの一つの連続した音源との対比を、`BmsonWriterContinuationRunsPreserveRestartAndSamePathChannelOverlap` は独立した再開・別のチャンネルの重なりを、`BmsonWriterLongNoteDoesNotCutAudioAndExplicitOrphanUpSoundsAtItsOwnPulse` はLNと明示したupを、出力したWAVの実際のフレーム・PCMで確認する。既存のBMSの長さ・末尾の無音の検査を維持する。 |
| 先読みでの形式の選択、一回の移譲、入力失敗の保留と致命的な失敗の所有 | [`NextSongPreloadOwner`](../../../BeMusicSeeker/Models/Playback/NextSongPreloadOwner.cs)、[`InternalBMSAutoPlayerSoundOnly`](../../../BeMusicSeeker/Models/Playback/InternalBMSAutoPlayerSoundOnly.cs) | [`NextSongPreloadOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/NextSongPreloadOwnerTests.cs) の `BmsonPreloadDispatchesModernOrLegacyAndKeepsInputFailureUntilAdoption` と、既存の取消・致命的な失敗・一回の移譲の検査。 |
| 形式の入口・外部プレイヤーの権限・保存の権限の境界 | 共通モデルへの投影、外部プレイヤーの選択、操作の権限 | [再生パネルの外部権限の検査](../ui/playback-panel.md#実装とテストの対応)、[共通モデルの権限・メニューのテスト](chart-model.md#実装とテストの対応)、[音声変換の選択入口の検査](../runtime/audio-conversion.md#実装とテストの対応)を参照する。 |
| 空でない発音だけによる音声の終端、通常のロード・準備済みの結果の採用・Writerの一致 | `BmsAudioFrameSchedule.Create`、`BMSAutoPlayer`、`BMSAutoPlayWriter` | `BMSAutoPlayWriterTests.BmsonAudioEndUsesOnlyNonemptyVoicesAcrossLoadPreparationAndWriter` は、後方の欠落・正常な0フレーム・空の音源窓で480フレーム・10ms、後方の再開・別のチャンネルで24480フレーム・510ms、空でない発音がない場合に0フレーム・0msを確認し、表示の終端・ノートの情報と既存のBMSの長さの検査を維持する。 |
| 初期線の保持・小節数・量子化された正の線・音声の時計の不変 | `BmsonPlaybackParser` / `PlaybackControlKind.InitialBarLine` / `BMSPlayer` | [`BmsonPlaybackParserTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Chart/BmsonPlaybackParserTests.cs) / [`BmsonPlaybackParserTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Chart/BmsonPlaybackParserTests.cs) の `GlobalPulseAndDisplayLinesDoNotChangeAudioClock` は、初期線0・1・2本と、LastMeasure・全音声区間を確認する。[`BMSPlayerControlTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/BMSPlayerControlTests.cs) の `BmsonInitialLinesDoNotAdvanceMeasureButQuantizedPositiveLinesDo` は、実際の Start での前進と、公開する CurrentTime での戻りのシークを分け、通常のBPMと巨大なBPMを確認する。 |

## 関連資料

- [bmson再生の設計判断](../../decisions/bmson-playback-boundaries.md)
- [BMSの時刻計算](ribbit-timing.md)
- [音声実行基盤](../runtime/audio.md)・[音声変換](../runtime/audio-conversion.md)
- [bmson 1.0.0仕様](https://github.com/bemusic/bmson-spec/blob/master/doc/index.rst)
