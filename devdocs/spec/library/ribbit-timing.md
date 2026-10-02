# Ribbit の BMS 時刻計算

## 目的と適用範囲

`Ribbit.BMS.BMSFile` が内蔵再生・音声書出し用に解析する BMS/BME/BML/PMS の数値入力と時刻を定めます。[譜面情報用パーサー](parser-compatibility.md)とは別の経路です。有限値の演算に近似や固定幅の上限を設けず、小節境界で整数 tick を確定します。

本番採用の判断と実譜面比較の根拠は[厳密な有理数によるBMS時刻計算の採用](../../decisions/ribbit-exact-timing-adoption.md)を参照します。

## 用語

共通語義は[用語集](../glossary.md)に従います。tick は100ns、小節内位置は通常小節の先頭を0・終端を1とする分数、小節長 `L` はその小節の長さ倍率です。`BmsNumber` は有限の `Fraction` と明示的な正の無限大を区別する入力境界です。未定義は外側の nullable で表します。

## 仕様

### 有限数と入力

`Fraction` は分子・分母を `BigInteger` で保持する不変の有限有理数です。常に既約・分母正・ゼロは論理的な `0/1` であり、`default` もゼロです。加減算は分母のGCD、乗除算は交差約分を使い、既約性が証明済みの結果を再約分しません。比較・等値・hashは近似へ落としません。decimal は係数とscaleから厳密に変換します。

BMSの有限十進文字列は係数と指数から厳密に生成します。旧 `double.TryParse` が正の無限大を返す `1e400` も有限数です。表示・再生接続用 `ToDouble()` の近似値を時刻計算や比較へ戻しません。巨大な分子・分母を個別double化せず、比のscaleを調整して変換します。

字句の受理と正値ガードは従来のパースに従います。`#BPM`・`#STOPxx`・`#xxx02` は現在カルチャの既定 `double.TryParse`、`#BPMxx` は既定 `decimal.TryParse` です。その値を演算のoracleにせず、受理した文字列から有限値を作ります。たとえば en-US の指数 `1e2` は前者で受理し、拡張BPMでは無視します。末尾正符号 `120+` は拡張BPMで受理し、前者では無視します。`1e-400` のように旧パースでゼロになる値は正値ガードにより無視します。無効な後続定義は前の定義を消しません。小節長02ではASCII空白を除去してから数値を解釈します。

明示的な正∞BPMの時間係数はゼロです。正∞BPM下で正∞STOPを参照した場合もSTOP増分はゼロです。有限BPM文字列を旧doubleパースが正∞に分類した場合（例: `1e400`）、明示正∞STOPの個別時間と累積への増分もゼロとして旧受理を維持します。入力時の分類を `BmsNumber` に保持し、この特殊STOPの作用だけに使います。分類は数学的等値・hash・大小比較に含めません。BPM03または拡張BPMで区間のBPMが変わると、分類もその値へ切り替わります。

有限STOPと通常の小節係数には厳密値を使います。BPM・小節長がともに `1e400` で先頭に明示∞STOPを置くと、個別STOP時間は0tick、中間位置1/2は1,200,000,000tick、終端は2,400,000,000tickです。STOPも有限文字列 `1e400` なら増分は12,500,000tick、中間位置は1,212,500,000tick、終端は2,412,500,000tickになります。

未参照の正∞STOP等を入力時に一括拒否しません。旧パースでも有限となるBPM下の正∞STOP参照は旧版と同様に失敗します。正∞小節長の時刻計算は正∞BPM下や旧正∞分類の有限BPM下でも旧版と同様に失敗します。有限tickにできない参照時の組合せは算術・範囲エラーとして表面化します。NaN・無限大の汎用算術を有限Fractionへ持ち込みません。除算後のlong範囲外やゼロ除算を別の値へ置き換えません。旧版で成功し新版で算術・範囲エラーになる実入力を見つけた場合は、受理互換性の問題として個別に調査します。

`InvalidBmsFileException.IsInputFailure` は、解析器がラップした不正データ、入力I/O、アクセス拒否、譜面時刻の算術失敗を通常の入力不良として分類します。先読み側がparser内部の例外一覧を別に持つことはしません。OOM、DLL、音声資源のfatal、原因不明・未設定のラップは入力不良へ変換せず、音声資源のfatal内に算術例外があっても分類を変えません。`#RANDOM 2147483647`の既存の上限計算失敗も、乱数呼出し境界で元の例外を保持して入力不良に分類します。通常範囲で乱数実装が投げる任意の`ArgumentOutOfRangeException`はこの分類へ広げません。入力の従来の拒否条件と内部原因は維持し、[次曲の先行準備](../runtime/audio.md#次曲一件の先行準備)では実採用時まで通常入力失敗を保留します。

### 小節内時間と制御順

直前の制御位置を `p0`、その制御後の分数tickを `q0`、BPMを `b` とします。

```text
区間係数 r = 2,400,000,000 × L / b
位置pの小節内時間 q = q0 + (p - p0) × r
STOP増分 = 12,500,000 × STOP値 / b
絶対tick = 小節開始tick + trunc(q)
```

`trunc` はゼロ方向の整数化です。STOPの増分には `L` を掛けず、個別表示用の整数STOP時間を小節内累積へ戻しません。区間係数は非零の位置進行に初めて必要になった時に取得し、有効BPM区間で再利用します。BPM変更時は取得済み係数を無効化するだけで、先行計算しません。距離0の時刻は変わらず、STOPは進行距離とは独立にその位置で評価します。小節ごとの状態は解析中のローカル変数へ集約し、ノーツには元の位置と確定絶対時刻だけを保持します。

同位置のすべてのノーツに制御前の時刻を付け、その後、小節線→BPM→拡張BPM→STOPの順に制御します。同種の順序は安定です。同位置の各制御は制御前の分数tickへ戻してから作用するため、STOPは最後の一つだけが後続へ作用します。最後のSTOPが未定義なら先行STOPの増分も後続に残りません。

次小節の開始は前小節線で確定した整数tickです。小節間の端数補償・全曲分数累積は行いません。BPM7・通常長7小節の境界は `7 × floor(2,400,000,000 / 7) = 2,399,999,994` tickです。BPMと小節長がともに有限 `1e400` の小節では、中間位置1/2は1,200,000,000tick、終端は2,400,000,000tickです。

ノーツ・08・09・地雷のデータは奇数の末尾1文字を無視します。03は末尾1文字もHex値として解釈し、位置1のイベントになり得ます。位置1の末尾BPM0も制御値・Min/Maxへ反映します。以後の計算がなければ通常小節の終端時刻を維持し、次小節の位置0で03/08から正BPMへ戻せます。復帰せず非零の位置進行が必要になればゼロ除算を表面化します。新しい構文検査で奇数長を拒否しません。LNや重複ノーツの解決規則は従来の `ResolveNoteConflict` を使います。

### 任意の解析診断

`ParseForAudit` は解析1回の `BmsParseOptions` を受け、実readのMD5・byte数・採用encodingを解析前にcallbackで通知します。追加のreadを行いません。乱数源を明示指定でき、既存Queue入口の選択規則を維持します。各RANDOMを履歴へ追加した直後に、Range/Value/Usedの一件の値を `RandomChoice` で一度だけ通知します。全履歴の反復コピーや終了時の重複通知を行わず、失敗途中でも完了済みの選択を回収できます。通知未指定の本番では通知用のコピーを作りません。

比較ツールは同じdecoderとfallbackを使う `DecodeForAudit` で一度だけ入力を準備し、`ParsePreparedForAudit` に本文・encoding・MD5・呼出し専用options/Queueを渡します。この同期入口全体がparseMillisecondsの対象です。通常ctor・Create・LoadFile・path版ParseForAuditの読込みや、空ファイルのInputRead通知順は維持します。cloneは従来どおりoptionsを引き継ぎません。

数値診断は任意です。入力の係数桁数・十進指数を巨大整数化前に通知し、選択した小節の開始・途中・完了、演算前の作業bit数上界、既約結果の分子絶対値bit数・分母bit数を区別します。失敗前の最後の観測を完了と解釈しません。診断未指定の本番ではbit数計算・高頻度ログを行いません。桁数取得のために巨大整数を文字列化しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 有限正規形、既定値、符号、任意精度演算、整数範囲、巨大比double、decimal scale | [Fraction](../../../BeMusicSeeker/Ribbit/Math/Fraction.cs) | [FractionTests](../../../BeMusicSeeker.Tests/Chart/FractionTests.cs) の4テストで独立した整数比と `2^160` の代数式を確認。 |
| 小節境界、tick直前の小数BPM、分数STOP、小節長とSTOP、有限overflow文字列、旧正値ガード | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs) | [RibbitBmsFileTimingTests](../../../BeMusicSeeker.Tests/Chart/RibbitBmsFileTimingTests.cs) の `MeasureBoundariesDiscardFractionalTicksInsteadOfCompensatingLater`、`DecimalBpmAboveOneTickBoundaryProducesZeroTicks`、`StopsAccumulateFractionalTicksAndIgnoreMeasureLengthMultiplier`、`FiniteOverflowTextRemainsExactAndUnderflowKeepsTheOldPositiveGuard`。 |
| 字句規則、無効上書き、ASCII空白、奇数末尾、同位置制御とSTOP、明示正∞BPMと特殊値参照の成功・失敗境界 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[BmsNumber](../../../BeMusicSeeker/Ribbit/BMS/BmsNumber.cs) | 同テストの `NumericChannelsKeepTheirExistingLexicalRules`、`DecimalAndGroupSeparatorsFollowTheCurrentCulture`、`OnlyMeasureLengthRemovesEmbeddedAsciiSpacesAndInvalidDefinitionsKeepPreviousValues`、`OddDataTailsKeepTheirChannelSpecificMeaning`、`SamePositionControlsStampNotesBeforeChangesAndOnlyLastStopAffectsLaterTime`、`ExplicitInfiniteBpmHasZeroCoefficientAndUnusedInfiniteStopIsAccepted`、`ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure`。 |
| 03奇数末尾BPM0の保持、係数が不要な終端・次小節先頭の正BPM復帰、非零進行でのゼロ除算 | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs) | 同時刻テストの `TerminalOddZeroBpmKeepsItsControlAndTheAlreadyStampedMeasureTime` は位置1のBAR/BPM時刻と値0を保持し、`NextMeasureStartCanRestorePositiveBpmBeforeAnyPositionAdvance` は03/08の復帰を各検査。`ZeroBpmStillFailsWhenTheNextMeasureNeedsNonzeroPositionAdvance` は未復帰の解析失敗を確認。 |
| 旧正∞分類の有限BPM下の特殊STOPゼロ作用、有限STOPと小節係数の厳密性、BPM変更時の分類切替 | [BmsNumber](../../../BeMusicSeeker/Ribbit/BMS/BmsNumber.cs)、`BMSFileTiming.TryParseNumber` / `CalculateMeasureTiming` | 同テストの `HugeFiniteBpmKeepsInfiniteStopZeroAndFiniteStopExact` は個別STOP・中点・終端tickと入力分類に依存しない等値/hashを確認し、`TempoChangeReplacesTheInitialInfiniteStopInputClassification` は03/08後の失敗を確認。`ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure` は有限 `1e400` BPM下の正∞小節長失敗も確認。 |
| 再生・書出しの時間接続、独立したframe期待値 | [BMSPlayer](../../../BeMusicSeeker/Ribbit/BMS/BMSPlayer.cs)、[BMSAutoPlayWriter](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayWriter.cs) | [BMSPlayerControlTests](../../../BeMusicSeeker.Tests/Playback/BMSPlayerControlTests.cs)、[BmsAudioFrameScheduleTests](../../../BeMusicSeeker.Tests/Playback/BmsAudioFrameScheduleTests.cs)、[BMSAutoPlayWriterTests](../../../BeMusicSeeker.Tests/Playback/BMSAutoPlayWriterTests.cs) 、[BmsRealtimeAudioSchedulerTests](../../../BeMusicSeeker.Tests/Playback/BmsRealtimeAudioSchedulerTests.cs) の `ParsedTempoStopAndMeasureChangesMatchIndependentWriterAndRealtimeFrames` の既存期待値を維持。 |
| 明示正∞STOP・小節長の失敗理由の表示言語 | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs)、[表示リソース](../../../BeMusicSeeker/Properties/Resources.resx)、[言語辞書](../../../lang) | [LocalizationResourceParityTests](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs) の全件検査でアクセサー・6言語のキーと非空値・日本語正本の一致を確認。上記の非有限参照テストで従来の失敗条件・例外種別を維持。 |
| 実入力識別、指定乱数・途中履歴、任意診断 | [BmsParseOptions](../../../BeMusicSeeker/Ribbit/BMS/BmsParseOptions.cs)、`BMSFile.ParseForAudit` | 同時刻テストの `AuditReportsEachUsedAndSkippedRandomChoiceOnceInOrder` と `AuditKeepsOnlyCompletedRandomChoicesWhenTheNextSelectionFails` が本番監査APIの順・回数と途中失敗の回収を直接検査し、比較ツールの準備済み本文入口は対象Runnerのビルドと差分点検で確認。実譜面比較に基づく性能・受理互換性の評価は[採用判断](../../decisions/ribbit-exact-timing-adoption.md)を参照。 |
| 乱数上限の既存拒否と通常入力失敗分類、任意の乱数故障の保持 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[NextSongPreloadOwner](../../../BeMusicSeeker/Models/Playback/NextSongPreloadOwner.cs) | [NextSongPreloadOwnerTests](../../../BeMusicSeeker.Tests/Playback/NextSongPreloadOwnerTests.cs)の`InputFailure_IsReturnedOnAdoptionWithoutRetry`と`DiscardedFailure_SuppressesOnlyOrdinaryInputErrors`は上限overflow入力を実parserで確認する。[RibbitBmsFileTimingTests](../../../BeMusicSeeker.Tests/Chart/RibbitBmsFileTimingTests.cs)の`AuditKeepsOnlyCompletedRandomChoicesWhenTheNextSelectionFails`は通常範囲の未分類故障・任意の引数例外を入力不良に変換しないことを確認する。 |

## 関連資料

- [音声再生](../runtime/audio.md)
- [譜面ファイルの読取り](chart-file-reading.md)
- [テスト検証](../development/testing.md)
