# Ribbit の BMS 時刻計算

## 目的と適用範囲

`Ribbit.BMS.BMSFile` が内蔵再生・音声書出しのために解析する BMS/BME/BML/PMS の、数値の入力と時刻を定めます。[譜面情報用の解析器](parser-compatibility.md)とは別の経路です。有限値の演算に近似や固定幅の上限を設けず、小節の境界で整数のtickを確定します。

bmsonは[専用の再生仕様](bmson-playback.md)に従います。全曲のpulseと固定刻み時刻を使うbmsonと再生経路を共通化しても、本書のBMSの入力・小節の境界・同位置の制御の規則は変えません。

本番に採用した判断と、実譜面での比較の根拠は[厳密な有理数によるBMS時刻計算の採用](../../decisions/ribbit-exact-timing-adoption.md)を参照します。

## 用語

共通の語義は[用語集](../glossary.md)に従います。機能固有の用語は次のとおりです。

| 用語 | 意味 |
| --- | --- |
| tick | 100ns。 |
| 小節内位置 | 通常の小節の先頭を0、終端を1とする分数。 |
| 小節長 `L` | その小節の長さの倍率。 |
| `Fraction` | 分子・分母を `BigInteger` で持つ、不変の有限の有理数。 |
| `BmsNumber` | 有限の `Fraction` と、明示的な正の無限大を区別する入力の境界。未定義は外側の nullable で表す。 |

## 仕様

### 有限数と入力

`Fraction` は常に既約で分母が正であり、ゼロは論理的な `0/1`、`default` もゼロです。比較・等値・ハッシュは近似へ落とさず、decimal は係数と位取りから厳密に変換します。

BMSの有限の十進文字列は、係数と指数から厳密に作ります。旧い `double.TryParse` が正の無限大を返す `1e400` も、有限の数として扱います。表示・再生への接続に使う `ToDouble()` の近似値を、時刻の計算や比較へ戻しません。

#### 字句の受理

字句の受理と正の値の検査は、固定のen-USで解析し、OSの地域設定で変えません。

| 定義 | 受理の規則 |
| --- | --- |
| `#BPM`、`#STOPxx`、`#xxx02` | `double.TryParse` の `Float \| AllowThousands` |
| `#BPMxx`（拡張BPM） | `decimal.TryParse` の `Number` |
| `#TOTAL`・`#PLAYLEVEL` のdouble、`#RANK`・`#DIFFICULTY`・RANDOM/IF・小節番号の整数、03の16進値 | 固定の書式で読み、既存の値域と、不正なときの既定値を維持する。 |

事前の検査と、係数・指数への分解でも同じ固定の書式を使い、小数点は `.`、桁区切りは `,`、符号と特殊値もen-USの扱いを保ちます。その解析結果は演算の基準にせず、受理した文字列から有限の値を作ります。

たとえば、en-USの指数 `1e2` は `double.TryParse` 系の定義では受理し、拡張BPMでは無視します。末尾の正符号 `120+` は拡張BPMで受理し、`double.TryParse` 系では無視します。`1e-400` のように旧い解析で0になる値は、正の値の検査で無視します。無効な後続の定義は、前の定義を消しません。小節長（02）では、ASCIIの空白を除いてから数値を解釈します。

#### 正の無限大

明示的な正の無限大のBPMの時間係数はゼロです。正の無限大のBPMの下で正の無限大のSTOPを参照した場合も、STOPの増分はゼロです。

有限のBPM文字列を旧いdoubleの解析が正の無限大に分類した場合（例: `1e400`）も、明示的な正の無限大のSTOPの個別の時間と累積への増分をゼロとし、旧い受理を維持します。この入力時の分類は `BmsNumber` に保持し、この特殊なSTOPの作用にだけ使います。分類は数学的な等値・ハッシュ・大小の比較に含めません。BPM03または拡張BPMで区間のBPMが変わると、分類もその値のものへ切り替わります。

有限のSTOPと通常の小節の係数には厳密な値を使います。BPM・小節長がともに `1e400` で、先頭に明示的な無限大のSTOPを置くと、個別のSTOPの時間は0tick、中間の位置1/2は1,200,000,000tick、終端は2,400,000,000tickです。STOPも有限の文字列 `1e400` なら、増分は12,500,000tick、中間の位置は1,212,500,000tick、終端は2,412,500,000tickです。

参照されない正の無限大のSTOPなどを、入力時に一括で拒否しません。次の参照は、旧い版と同じく失敗します。

- 旧い解析でも有限となるBPMの下での、正の無限大のSTOPの参照
- 正の無限大の小節長の時刻計算（正の無限大のBPMの下や、旧い解析で正の無限大に分類した有限のBPMの下でも失敗する）

有限のtickにできない参照時の組合せは、算術・範囲のエラーとして表に出します。NaN・無限大の汎用の算術を、有限の `Fraction` へ持ち込みません。除算後のlongの範囲外やゼロ除算を、別の値へ置き換えません。旧い版で成功し、新しい版で算術・範囲のエラーになる実際の入力が見つかった場合は、受理の互換性の問題として個別に調査します。

#### 入力不良の分類

`InvalidBmsFileException.IsInputFailure` は、解析器が包んだ不正なデータ、入力の入出力、アクセスの拒否、譜面の時刻の算術の失敗を、通常の入力不良として分類します。先読み側が、解析器の内部の例外の一覧を別に持つことはしません。

OOM、DLL、音声資源の致命的な失敗、原因不明・未設定の包まれた例外は入力不良へ変換せず、音声資源の致命的な失敗の中に算術の例外があっても分類を変えません。`#RANDOM 2147483647` での既存の上限計算の失敗は、乱数の呼出しの境界で元の例外を保持したまま入力不良に分類します。通常の範囲で乱数の実装が投げる任意の `ArgumentOutOfRangeException` は、この分類へ広げません。

入力の拒否の条件と内部の原因は維持し、[次曲の先行準備](../runtime/audio.md#次曲一件の先行準備)では、通常の入力失敗を実際に採用するときまで保留します。

### 小節内時間と制御順

直前の制御の位置を `p0`、その制御の後の分数のtickを `q0`、BPMを `b` とします。

```text
区間係数 r = 2,400,000,000 × L / b
位置pの小節内時間 q = q0 + (p - p0) × r
STOP増分 = 12,500,000 × STOP値 / b
絶対tick = 小節開始tick + trunc(q)
```

`trunc` はゼロ方向の整数化です。STOPの増分には `L` を掛けず、個別の表示用の整数のSTOP時間を、小節内の累積へ戻しません。距離0の位置の時刻は変わらず、STOPは進行の距離とは独立に、その位置で評価します。ノーツには元の位置と確定した絶対時刻だけを保持します。

同じ位置のすべてのノーツに制御前の時刻を付け、その後、小節線→BPM→拡張BPM→STOPの順に制御します。同じ種類の中の順序は安定です。同位置の各制御は制御前の分数のtickへ戻してから作用するため、STOPは最後の一つだけが後続へ作用します。最後のSTOPが未定義なら、先行するSTOPの増分も後続に残りません。

次の小節の開始は、前の小節線で確定した整数のtickです。小節間の端数の補償や、全曲での分数の累積は行いません。BPM 7・通常の長さの7小節の境界は `7 × floor(2,400,000,000 / 7) = 2,399,999,994` tickです。BPMと小節長がともに有限の `1e400` の小節では、中間の位置1/2は1,200,000,000tick、終端は2,400,000,000tickです。

#### データ末尾と0のBPM

ノーツ・08・09・地雷のデータは、奇数個の末尾の1文字を無視します。03は末尾の1文字も16進値として解釈し、位置1のイベントになり得ます。位置1の末尾のBPM 0も、制御の値と最小・最大へ反映します。

その後に計算がなければ、通常の小節の終端時刻を維持し、次の小節の位置0で03・08から正のBPMへ戻せます。戻らないまま0でない位置の進行が必要になれば、ゼロ除算を表に出します。奇数長を新しい構文の検査で拒否しません。LNや重複したノーツの解決規則は、既存の `ResolveNoteConflict` を使います。

### 任意の解析診断

`ParseForAudit` は解析1回分の `BmsParseOptions` を受け、実際に読んだ入力のMD5・バイト数・採用した文字コードを、解析の前にコールバックで通知します。追加の読取りは行いません。乱数源を明示的に指定でき、既存のQueueの入口の選択規則を維持します。各RANDOMを履歴へ追加した直後に、Range・Value・Used の一件の値を `RandomChoice` で一度だけ通知します。全履歴の反復のコピーや、終了時の重複した通知は行わず、途中で失敗しても完了済みの選択を回収できます。通知を指定しない本番では、通知用のコピーを作りません。

比較ツールは、同じ復号器と代替の規則を使う `DecodeForAudit` で一度だけ入力を準備し、`ParsePreparedForAudit` に本文・文字コード・MD5と、呼出し専用の options・Queue を渡します。この同期の入口の全体が parseMilliseconds の計測対象です。通常のコンストラクター・Create・LoadFile・パス版の ParseForAudit の読込みと、空ファイルでの InputRead の通知の順は変えません。複製（clone）は options を引き継ぎません。

数値の診断は任意です。入力の係数の桁数と十進指数を巨大な整数にする前に通知し、選択した小節の開始・途中・完了、演算前の作業のビット数の上限、既約な結果の分子の絶対値と分母のビット数を区別します。失敗の前の最後の観測を、完了と解釈しません。診断を指定しない本番では、ビット数の計算や高頻度のログを行いません。桁数を得るために巨大な整数を文字列化しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 有限の正規形、既定値、符号、任意精度の演算、整数の範囲、巨大な比のdouble化、decimalの位取り | [Fraction](../../../BeMusicSeeker/Ribbit/Math/Fraction.cs) | [FractionTests](../../../tests/BeMusicSeeker.Core.Tests/Chart/FractionTests.cs) が、独立した整数比と `2^160` の代数式で確認する。 |
| 小節の境界、tickの直前の小数のBPM、分数のSTOP、小節長とSTOP、有限であふれる文字列、旧い正の値の検査 | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs) | [RibbitBmsFileTimingTests](../../../tests/BeMusicSeeker.Core.Tests/Chart/RibbitBmsFileTimingTests.cs) の `MeasureBoundariesDiscardFractionalTicksInsteadOfCompensatingLater`、`DecimalBpmAboveOneTickBoundaryProducesZeroTicks`、`StopsAccumulateFractionalTicksAndIgnoreMeasureLengthMultiplier`、`FiniteOverflowTextRemainsExactAndUnderflowKeepsTheOldPositiveGuard`。 |
| 字句の規則、無効な上書き、ASCIIの空白、奇数の末尾、同位置の制御とSTOP、明示的な正の無限大のBPMと特殊値の参照の成功・失敗の境界 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[BmsNumber](../../../BeMusicSeeker/Ribbit/BMS/BmsNumber.cs) | 同じテストの `NumericChannelsKeepTheirExistingLexicalRules`、`NumericInputAndEventTicksAreIndependentOfCurrentCulture`、`OnlyMeasureLengthRemovesEmbeddedAsciiSpacesAndInvalidDefinitionsKeepPreviousValues`、`OddDataTailsKeepTheirChannelSpecificMeaning`、`SamePositionControlsStampNotesBeforeChangesAndOnlyLastStopAffectsLaterTime`、`ExplicitInfiniteBpmHasZeroCoefficientAndUnusedInfiniteStopIsAccepted`、`ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure`。 |
| 数値の入力の地域非依存、小数点・桁区切り・符号・特殊値と整数の分岐 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs)、[StringExt](../../../BeMusicSeeker/Ribbit/Util/Extensions/StringExt.cs) | 同じテストの `NumericInputAndEventTicksAreIndependentOfCurrentCulture` は、同じ入力を ja-JP/en-US/de-DE/fr-FR/th-TH/ar-SA と符号を変えたカルチャへ渡し、数値・発音・STOP・終端のtickを検査する。`NumericChannelsKeepTheirExistingLexicalRules` は同じ字句の境界を、`ExplicitInfiniteBpmHasZeroCoefficientAndUnusedInfiniteStopIsAccepted` は同じ特殊値の異なるカルチャでの扱いを検査する。 |
| 03の奇数の末尾のBPM 0の保持、係数を使わない終端・次の小節の先頭での正のBPMへの復帰、0でない進行でのゼロ除算 | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs) | 同じ時刻のテストの `TerminalOddZeroBpmKeepsItsControlAndTheAlreadyStampedMeasureTime` は位置1の小節線・BPMの時刻と値0の保持を、`NextMeasureStartCanRestorePositiveBpmBeforeAnyPositionAdvance` は03・08での復帰を検査する。`ZeroBpmStillFailsWhenTheNextMeasureNeedsNonzeroPositionAdvance` は、復帰しない場合の解析失敗を確認する。 |
| 旧い解析で正の無限大に分類した有限のBPMの下での特殊なSTOPのゼロの作用、有限のSTOPと小節の係数の厳密性、BPM変更時の分類の切替え | [BmsNumber](../../../BeMusicSeeker/Ribbit/BMS/BmsNumber.cs)、`BMSFileTiming.TryParseNumber` / `CalculateMeasureTiming` | 同じテストの `HugeFiniteBpmKeepsInfiniteStopZeroAndFiniteStopExact` は、個別のSTOP・中間・終端のtickと、入力の分類に依存しない等値・ハッシュを確認し、`TempoChangeReplacesTheInitialInfiniteStopInputClassification` は03・08の後の失敗を確認する。`ReferencedInfiniteLengthAndFiniteBpmInfiniteStopPreserveParsingFailure` は、有限の `1e400` のBPMの下での正の無限大の小節長の失敗も確認する。 |
| 再生・書出しの時間の接続、独立したフレームの期待値 | [BMSPlayer](../../../BeMusicSeeker/Ribbit/BMS/BMSPlayer.cs)、[BMSAutoPlayWriter](../../../BeMusicSeeker/Ribbit/BMS/BMSAutoPlayWriter.cs) | [BMSPlayerControlTests](../../../tests/BeMusicSeeker.Core.Tests/Playback/BMSPlayerControlTests.cs)、[BmsAudioFrameScheduleTests](../../../tests/BeMusicSeeker.Core.Tests/Playback/BmsAudioFrameScheduleTests.cs)、[BMSAutoPlayWriterTests](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BMSAutoPlayWriterTests.cs)、[BmsRealtimeAudioSchedulerTests](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BmsRealtimeAudioSchedulerTests.cs) の `ParsedTempoStopAndMeasureChangesMatchIndependentWriterAndRealtimeFrames` の既存の期待値を維持する。 |
| 明示的な正の無限大のSTOP・小節長の失敗理由の表示言語 | [BMSFileTiming](../../../BeMusicSeeker/Ribbit/BMS/BMSFileTiming.cs)、[表示リソース](../../../BeMusicSeeker/Properties/Resources.resx)、[言語辞書](../../../lang) | [LocalizationResourceParityTests](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs) の全件の検査で、アクセサー・6言語のキーと空でない値・日本語の正本との一致を確認する。上の有限でない参照のテストで、失敗の条件と例外の種類を維持する。 |
| 実際の入力の識別、指定した乱数・途中の履歴、任意の診断 | [BmsParseOptions](../../../BeMusicSeeker/Ribbit/BMS/BmsParseOptions.cs)、`BMSFile.ParseForAudit` | 同じ時刻のテストの `AuditReportsEachUsedAndSkippedRandomChoiceOnceInOrder` と `AuditKeepsOnlyCompletedRandomChoicesWhenTheNextSelectionFails` が、本番の監査APIの順序・回数と途中の失敗での回収を直接検査する。比較ツールの準備済み本文の入口は、対象の実行ツールのビルドと差分の点検で確認する。実譜面の比較に基づく性能・受理の互換性の評価は[採用判断](../../decisions/ribbit-exact-timing-adoption.md)を参照する。 |
| 乱数の上限の既存の拒否と通常の入力失敗の分類、任意の乱数の故障の保持 | [BMSFile](../../../BeMusicSeeker/Ribbit/BMS/BMSFile.cs)、[NextSongPreloadOwner](../../../BeMusicSeeker/Models/Playback/NextSongPreloadOwner.cs) | [NextSongPreloadOwnerTests](../../../tests/BeMusicSeeker.Core.Tests/Playback/NextSongPreloadOwnerTests.cs) の `InputFailure_IsReturnedOnAdoptionWithoutRetry` と `DiscardedFailure_SuppressesOnlyOrdinaryInputErrors` が、上限であふれる入力を実際の解析器で確認する。[RibbitBmsFileTimingTests](../../../tests/BeMusicSeeker.Core.Tests/Chart/RibbitBmsFileTimingTests.cs) の `AuditKeepsOnlyCompletedRandomChoicesWhenTheNextSelectionFails` は、通常の範囲での分類されない故障・任意の引数の例外を入力不良に変換しないことを確認する。 |

## 関連資料

- [音声再生](../runtime/audio.md)
- [譜面ファイルの読取り](chart-file-reading.md)
- [テスト検証](../development/testing.md)
