# chart_info parser compatibility notes

最終更新: 2026-04-27

この文書は、`chart_info` 生成で beatoraja / jbms-parser 互換を目指す際に確認した実装上の注意点をまとめる。
一般的な BMS / BMSON 仕様から自然には読めない、参照実装固有の解釈や Java 実装由来の挙動を優先して記録する。

対象実装:

- 本アプリ: `BeMusicSeeker/Models/BmsLibraryInternal/ChartInfoParser.cs`
- BMS 参照: `jbms-parser` の `BMSDecoder` / `Section` / `BMSModel` / `TimeLine`
- BMSON 参照: `jbms-parser` の `BMSONDecoder`
- 情報計算参照: beatoraja / songdata-updater の `SongInformation`
- chart string 参照: `BMSModel.toChartString()`

## 基本方針

- `chart_info` は「所持譜面管理」ではなく「譜面メタデータ」の保存先として扱う。
- `song` テーブルは LR2 互換維持のため変更しない。
- 解析結果は原則として beatoraja / jbms-parser の解釈に寄せる。
- ただし RANDOM 譜面は参照 DB 側の過去選択分岐と完全一致しないため、値差分は許容する。
- `chart_info` では deterministic metadata を優先し、RANDOM はまず branch 1 固定で解析し、recoverable failure の場合だけ fallback branch を試す。
- fatal parse failure だけを `install-performance.log` に WARN 出力する。non-fatal warning 相当は diagnostic 扱いにし、通常ログには出さない。

## BMS テキスト読み取り

### 既定エンコード

BMS の既定 decode は MS932 系に寄せる。
通常の `chart_info` 生成では、アプリが独自に推定・補正した `maintenance.encoding` は使わない。

理由:

- beatoraja / jbms-parser は日本語 BMS の実運用を前提に MS932 系として読まれることが多い。
- UTF-8 として読めても、タイトル・サブタイトル・DIFFICULTY 推定文字列などが変わると結果がずれる。
- `maintenance.encoding` は DataGrid 表示や LR2 `song` テーブルの文字列補正用であり、譜面構文の参照実装互換 decode を制御するものではない。
- 例えば `#PLAYLEVEL １` を含む MS932 譜面で表示用 encoding が `ks_c_5601-1987?` と推定されても、`chart_info` では MS932 として解釈し、全角数字 `１` を Java `Integer.parseInt` 相当で `1` にする必要がある。

`ChartInfoParser` の `encodingName` 引数は低レベル検証用 override として残しているが、通常 backfill からは常に `null` を渡す。

### 行コマンドの許容

`BMSDecoder` は reserve word をかなり位置依存で扱う。一般的な「空白区切りの key/value」としてだけ読むと差分が出る。

対応すべき例:

- `#DIFFICULTY 2`
- `#DIFFICULTY=2`
- `#TITLExxx` のような古い無空白構文
- `#BPMxx value` / `#STOPxx value` / `#SCROLLxx value`

実装上は reserve word に対して `substring(command.length + 2)` 相当の切り出しを使う場面がある。
`#DIFFICULTY=2` は `#` + `DIFFICULTY` + 区切り 1 文字 + argument という形として処理する。

### チャンネル行の colon 後空白

チャンネル行では colon 後の空白を trim してはいけない。

参照実装の `Section.processData` は概ね次の考え方で token を切る:

```java
int findex = line.indexOf(":") + 1;
int split = (line.length() - findex) / 2;
token = line.charAt(findex + i * 2), line.charAt(findex + i * 2 + 1);
```

つまり、`#01301: 100...` のように colon 後に空白がある場合、最初の token は `" 1"` になり、不正値として無視される。
C# 側で `:\s*(.*)` のように空白を食べると、本来無視されるノートが有効化される。

現在の方針:

- `#mmmcc\s*:(.*)` は許容する。
- `:` の後はそのまま保持する。
- data token は 2 文字単位で切る。
- 余った末尾 1 文字は参照実装同様に無視する。

## 数値 parse

### Java `Integer.parseInt` 相当

`#PLAYLEVEL`, `#DIFFICULTY`, `#RANK`, `#DEFEXRANK`, `#LNMODE`, `#BASE`, `#RANDOM`, `#IF` などの int 系は Java `Integer.parseInt` 相当に寄せる。

重要点:

- 文字列全体が int として valid でなければ invalid。
- `12abc` や `12.5` は先頭整数 `12` として扱わない。
- Java の `Integer.parseInt` は Unicode decimal digit を受ける。
  - 例: `#PLAYLEVEL ４`
  - 例: `#PLAYLEVEL 2８`
- overflow は invalid。

`level` は `chart_info` では nullable とし、未定義 / invalid は `NULL` にする。
beatoraja DB では未定義相当が `0` になるため、互換比較では `0 == NULL` として扱う。

### double 系

`#TOTAL`, `#BPM`, `#BPMxx`, `#STOPxx`, `#SCROLLxx` は trailing garbage を許容しない。
`#TOTAL 100abc` は未定義 / invalid 扱いにする。

通常の decimal / signed / exponent 形式は許容する。

double の値変換は Java 17 の `Double.parseDouble` 相当に寄せる必要がある。
.NET の `double.TryParse(..., InvariantCulture)` と JDK17 は、多桁 decimal を binary64 に丸める境界で 1 ulp ずれることがある。

観測例:

- `114.15384615384615384615384615`
  - JDK17: `114.15384615384616`
  - .NET parser: `114.15384615384615` になる環境がある
- BMSON JSON の `131.4889812233735`
  - JDK17: `131.4889812233735`
  - Json.NET 経由の .NET double: `131.48898122337351` になる環境がある

この 1 ulp 差は、`speedchange` の文字列と `BMSModel.toChartString()` 相当の BPM 出力にそのまま出るため、`charthash` も連動してずれる。
そのため本アプリでは JDK17 互換の decimal / hex floating parser を使い、round-to-nearest-even の結果を raw bits レベルで合わせる。

Java `Double.parseDouble` 互換として注意する点:

- trim は Java 的に `U+0020` 以下の前後文字を落とす。
- `NaN`, `Infinity`, `-Infinity` を受ける。
- 16進浮動小数点表記を受ける。
  - 例: `0x1.8p1`
- numeric suffix `f/F/d/D` を受ける。
- 多桁 decimal は exact rational として丸め、境界では round-to-nearest-even にする。

BMS 側では `#BPM`, `#BPMxx`, `#STOPxx`, `#SCROLLxx`, `#TOTAL`, 小節長 `#xxx02` に適用する。
BMSON 側では Json.NET が数値 token を先に .NET double 化してしまうため、converter だけでは足りない。
`info.init_bpm`, `info.total`, `bpm_events[].bpm`, `stop_events[].duration`, `scroll_events[].rate`, `mine_channels/key_channels[].notes[].damage` は raw JSON 文字列を走査し、JDK17 互換 parser で読み直してモデル値を上書きする。

`#TOTAL` は未定義でも fatal ではない。
本アプリでは:

- `total`: 表示・ソート用の有効値を保存する
- `total_defined`: `#TOTAL` / bmson total が明示されていたかを保存する

未定義時の `total` は jbms-parser と同様の default 計算値を保存し、UI 側で `total_defined=false` を警告表示に使う想定。

### BPM の min/max

beatoraja `song` DB の `maxbpm` / `minbpm` は整数列として保存されるため、比較時は整数部比較が必要になる。
本アプリの `chart_info` では小数 BPM 自体は保持する。

ただし、巨大 BPM が Java 側で `double -> int` 保存時に飽和 / narrowing 相当になるケースがあるため、`chart_info` 保存値も int 範囲外の巨大値は Java DB 比較に寄せた clamp を行う。

`minbpm` / `maxbpm` の候補には `InitialBpm` も含める。
`#BPM` 未定義でも timeline 0 の BPM change が正なら解析成功にする方針を取っているため、`InitialBpm = 0` が min 候補に残ることがある。

## `#BASE 62`

`#BASE 62` は indexed command / channel data token の基数に影響する。

注意点:

- channel id 自体は `#mmmcc` の `cc` を base36 的に見る箇所がある。
- data token は base36 / base62 を切り替える。
- `#BPM` short channel (`#mmm03`) は、base62 時に一度 base62 値を base62 文字列へ戻して base36 として再解釈する、という参照実装由来の特殊挙動がある。
- mine damage も base62 時に同種の再計算を行う。

この挙動は直感的ではないが、最新 jbms-parser の解釈結果に寄せるため維持する。

## RANDOM / conditional

### `#RANDOM/#IF/#ENDIF/#ENDRANDOM`

参照実装は `selectedRandoms` 未指定の場合にランダム選択する。
`chart_info` では stable metadata を優先するため、初回候補はすべて branch `1` とする。

RANDOM feature の判定は「`#RANDOM` らしい行を見たか」ではなく、「`#RANDOM` の引数を Java `Integer.parseInt` 相当に parse でき、参照実装の `model.random` に入るか」で行う。

重要な例:

- `#RANDOM 4` は valid。RANDOM stack に入り、`feature & 4` が立つ。
- `#RANDOM4` は reserve word としては拾われるが、参照実装では `line.substring(8).trim()` が空になり、`#RANDOMに数字が定義されていません` warning で終わる。`model.random` には入らないので `feature & 4` は立てない。
- この状態で `#IF` / `#ENDIF` が続く場合も、それぞれ warning / stack mismatch 扱いであり、RANDOM 使用譜面としては扱わない。
- `#ENDIF` / `#ENDRANDOM` は引数を持たない directive として、行頭 prefix だけで認識する。ここを `#IF` などの引数付き reserve word と同じ判定にすると、完全一致の `#ENDIF` が拾えず、skip stack が残って後続の MAIN DATA FIELD まで無視される。
- `#ENDRANDOM` が無い譜面も存在する。参照実装と同じく、`#IF/#ENDIF` の skip stack が閉じていれば、その後続行は通常通り処理する。

RANDOM retry:

- all `1`
- all `min(2,max)`
- all `min(3,max)`
- all `min(4,max)`
- all `max`
- sha256 / md5 seed candidate

branch は `1..n` に丸める。

retry 対象:

- initial BPM 不正
- timeline / distribution が overflow 級
- RANDOM branch 1 が異常に長い timeline を選ぶケース

最終失敗時のみ WARN を出す。retry 中の失敗は WARN にしない。

### `#SWITCH/#CASE/#SKIP/#ENDSW`

最新の jbms-parser / beatoraja が利用する `BMSDecoder` では、`#SWITCH/#CASE/#SKIP/#ENDSW` は特別処理されない。未知 command と同じ扱いになり、別途 `#IF` skip がかかっていない限り、ブロック内の譜面行は通常通り処理される。

そのため、本アプリ側でも `#SWITCH` 系の独自 conditional stack は実装しない。将来の参照実装で対応が入った場合のみ、production DB compare の差分を見ながら追従する。

## mode / lane assign

BMS の mode は、明示値だけでなく使用 channel によって 5K -> 7K, 5/7K -> 10/14K へ昇格する。
参照実装では `Section` 構築時に note channel を見て `model.setMode(...)` する。

lane assign は mode によって変わる。

- BEAT 5K
- BEAT 7K
- BEAT 10K
- BEAT 14K
- POPN 9K

特に 7K / 14K は scratch lane と key lane の並びが直感的な channel 順と一致しない。
`lanenotes` 差分を追うときは、内部 lane index と DataGrid 表示順を混同しない。

## timeline / time / length

### 内部時刻

参照実装は概ね次の流れで timeline time を作る。

```java
double time = previousPreciseTime
    + previousTimeline.getMicroStop()
    + 240000.0 * 1000 * (section - previousSection) / bpm;
TimeLine tl = new TimeLine(section, (long) time, keyCount);
tlcache.put(section, new TimeLineCache(time, tl));
```

重要点:

- 累積用の precise time は `double`。
- `TimeLine` に入る時だけ `(long)` で microseconds に切り詰める。
- Java の `(long)` は 0 方向 truncation。
- `TimeLine.getTime()` は `(int)(time / 1000)`。
- `BMSModel.getLastTime()` は最後の event timeline の `getMilliTime()` を int 化する。

C# 側で round したり、millisecond 単位で累積したりすると、`length`, `speedchange`, `distribution`, `density` が連動してずれる。

### section rate の扱い

`#xxx02` の小節長は、各 `Section` が保持する `rate` をそのまま note / BPM / STOP / SCROLL の section 座標計算に使う。

重要点:

- `Section` の `sectionnum` は前小節の `rate` を足して決まる。
- その小節内の event 位置は `sectionnum + pos * rate`。
- C# 側で `sectionStarts[next] - sectionStarts[current]` から rate を逆算すると、double 累積誤差で 1ms 前後の差が出る。
- 特に `#xxx02:0.9` のような小節が連続すると、`length`, `speedchange`, `distribution`, `density` がまとめてずれる。

したがって本アプリでは、section start 配列とは別に「その小節に解析された rate」を保持し、`ApplyEvents` / note line 展開に使う。
これは `Section.rate` を持つ参照実装に寄せるための対応であり、一般的な BMS 仕様だけを見ると見落としやすい。

### timeline 順序

参照実装の BMS timeline は `TreeMap<Double, TimeLine>` の section order が基準になる。
`SongInformation` も `model.getAllTimeLines()` を順に走査する。

時刻順に並べ替えると、負 BPM / STOP / SCROLL などの特殊譜面で speedchange や mainbpm の集計順が変わり得る。
ただし本アプリ側では UI / chart string / hash との兼ね合いがあるため、変更時は production diff fixture で確認する。

### 不正な「小節風」行と max section

`BMSDecoder` は、行が `#` + 3 桁数字で始まり、かつ行長が 7 文字以上なら、channel として壊れていても `lines[bar_index]` に入れて `maxsec` を更新する。

例:

```text
#187だいすき
#0736:bd
```

この挙動の結果:

- channel としては invalid / warning 相当でも、空の `Section` が末尾まで作られる。
- 各空 section に section line timeline が作られる。
- `BMSModel.getLastTime()` は最後の note / BGA / background を見るため必ず伸びるわけではない。
- ただし `SongInformation.speedchange` の末尾補完は `tls[tls.length - 1].getTime()` を見るため、最後の speed entry の時刻が大きく伸びることがある。

`length` は一致しているのに `speedchange` の末尾だけ `439999.0` のように伸びる差分は、この挙動が原因になり得る。
本アプリでも、channel line として有効かどうかとは別に、`#NNN...` 形式の chart-like line を見た時点で max section を更新する。

### 24時間超 timeline

24時間超は参照実装では異常扱いしない。
本アプリでも 24時間上限は置かず、解析できる限り `chart_info` 生成を試みる。

fatal にするケース:

- `length` が int millisecond に収まらない
- distribution bucket 数が int / allocation として現実的でない
- 参照実装でも overflow 相当になると判断できるケース

RANDOM 譜面では、巨大 timeline / distribution 配列長エラーは recoverable failure とし、RANDOM retry に進む。

## LN / LNOBJ

LN 処理は `Section.makeTimeLines` の分岐順に寄せる必要がある。
ここは notes count, lane notes, distribution, charthash すべてに影響する。

### `#LNOBJ`

`#LNOBJ` は通常ノート channel 上の特定 wav id を LN 終端として扱う。

参照挙動:

- LNOBJ を見つけたら、同一 lane の直前 note を後ろ向きに探す。
- 直前 note が NormalNote なら、それを LongNote 始点へ差し替える。
- LNOBJ 位置に LongNote 終端を置く。
- 直前 note が未ペア LongNote なら、その LongNote に終端を付ける。
- 対応できない場合は warning 相当で打ち切る。

### LN channel

LN channel (`#xxx5y` / `#xxx6y`) は開始・終端を交互に処理する。

参照挙動の要点:

- 既存 LN 範囲内かどうかは inclusive:
  - `ln.section <= section && section <= ln.pair.section`
- LN 開始位置に通常ノートがある場合:
  - LongNote で上書きする。
  - wav が異なる NormalNote は background note へ移す。
- LN 終端処理では、開始位置まで後ろ向きに timeline を辿る。
  - 開始位置より後ろに note がある場合、その note を消す。
  - 消した note が NormalNote なら background note へ移す。
  - 開始位置を見つけた場合のみ pair を作る。
  - 開始位置を見つけられない場合、pair を作らない。
- 未閉じ LN は最後に開始 note を消す。

最後の「開始位置を見つけた場合のみ pair を作る」が重要。
逆順定義などで開始 timeline が scan 範囲に無い場合、C# 側で無条件に pair を作ると、参照実装では未閉じとして消える LN が残り、`ln` / `n` / `notes` / `lanenotes` がずれる。

### LN 内 LN

既存 LN 範囲内に LN channel が現れた場合、参照実装は sentinel 的な `Double.MIN_VALUE` section の LongNote を `startln` に入れて、次の LN channel で toggle 的に消す。

意味:

- LN 内の LN 開始は通常の LN として count しない。
- 次の LN channel で sentinel を消して終わる。
- sentinel ではない未ペア LN が残っていた場合は、対応 timeline の note を null にすることがある。

### mine と LN

mine は以下の場合に置かない:

- 同時刻同 lane に note がある
- 既存 LN 範囲内にある

base62 時の mine damage は前述の特殊再計算を行う。

## distribution / density / enddensity

参照は `SongInformation` の計算に寄せる。

概略:

- `data = new int[model.getLastTime() / 1000 + 2][7]`
- 各 note は `tl.getTime() / 1000` に入る。
- LN は始点から終点秒まで LN density 用 bucket を fill する。
- LN 終端は `LNTYPE_LONGNOTE` 相当では count 対象から除外される。
- `borderPosition` は `totalNotes * (1 - 100 / total)` の通過位置から取る。
- `density` は threshold 以上の秒 bucket だけ平均する。
- `peakdensity` は playable notes の秒 bucket 最大。
- `enddensity` は border 以後の rolling window 最大。

実装注意:

- 秒 bucket は Java の `tl.getTime()` を基準にする。
- `length` が 1ms ずれるだけでも、bucket 数や境界秒が変わり density がずれることがある。
- distribution string は大量差分になりやすいので、まず note count / timeline time / LN 処理を疑う。

## speedchange

`speedchange` は `SongInformation` の `speedchange` 相当。

参照挙動:

- 初期値として `initialBpm,0.0` を入れる。
- timeline ごとに `bpm * scroll` を見る。
- STOP 中は speed `0.0` として扱う。
- 変化時刻は Java の `tl.getTime()`。
- 最後の speed entry が最後の timeline 時刻と違う場合、最後の timeline 時刻の entry を追加する。
- 文字列化は Java `StringBuilder.append(double)`、つまり `Double.toString`。

差分分類:

- time only diff:
  - 1ms 差など。timeline time / length と同根のことが多い。
- count diff:
  - STOP / BPM / SCROLL event ordering や duplicate timeline の処理差を疑う。
- format only diff:
  - Java `Double.toString` と C# formatting 差。
  - 例: `1.0E-4` と `0.0001E0` のような表記差。
  - 例: `7.6999669989E7` と `7.699966998899999E7` のような最短表現差。

本アプリでは `speedchange`, chart string, `TOTAL`, mine damage などの double 出力に JDK 21 `Double.toString` 相当の formatter を使う。
beatoraja / songdata-updater の production DB は `D:\beatoraja\jre\bin\java.exe`、つまり JDK 21 runtime で生成されているため、JDK 17 `FloatingDecimal` 系ではなく JDK 19+ `DoubleToDecimal` 系の互換をターゲットにする。
旧 `JavaDoubleToStringJdk17` は、古い runtime 由来の差分調査用として残している。

formatter は BMSON `charthash` にも影響するため、変更時は BMS / BMSON 双方の chart string fixture を見る。

2026-04-26 時点の production diff では、JDK 17 `Double.toString` 互換 formatter 適用後に `speedchange` / `charthash` が 11 件ずつ残った。
この時点の主因は parse 段階の `Double.parseDouble` 丸め差だった。

- BMS の高精度 decimal BPM を `double` に parse する段階の丸め差。
  - 例: `114.15384615384615384615384615` は Java `Double.parseDouble` 由来の値と .NET `double.Parse` 由来の値が 1 ulp ずれることがある。
- BMSON の JSON numeric literal を Json.NET が先に .NET double 化する段階の丸め差。
  - 例: `131.4889812233735` は Java と .NET で 1 ulp ずれることがある。

その後に残った charthash-only 2 件は JDK 17 / JDK 21 の `Double.toString` 差だった。
JDK 17 と JDK 21 では同じ binary64 値でも最短 decimal の選び方が異なることがある。

代表例:

- JDK 17: `1.14514191981036442E18`
- JDK 21: `1.1451419198103644E18`
- JDK 17: `8.4929057819839846E17`
- JDK 21: `8.492905781983985E17`
- JDK 17: `1.9999999999999998E23`
- JDK 21: `2.0E23`

`length`, `distribution`, `density`, `peakdensity`, `enddensity` が 0 件まで縮んだ状態で `speedchange` だけ残る場合、timeline bucket ではなく上記を優先して疑う。

## mainbpm

`mainbpm` は単純な最大滞在時間 BPM ではなく、`SongInformation` と同じく BPM ごとの `tl.getTotalNotes()` 集計で選ぶ。

注意点:

- key は `double` BPM。
- Java `HashMap<Double, Integer>` の iteration order に tie-break が依存することがある。
- 本アプリでは Java HashMap bucket order を再現する helper を使う。

## charthash / chart string

`charthash` は `BMSModel.toChartString()` 相当の文字列を SHA-256 化する。

chart string の主な要素:

- `JUDGERANK`
- `TOTAL`
- `LNMODE`
- timeline time
- BPM change
- STOP
- section line
- lane note state
- mine damage
- LN marker + audio duration

注意点:

- BPM 文字列は保存元文字列ではなく `tl.getBPM()` の double 文字列。
- TOTAL, BPM, STOP, mine damage, speedchange などは Java double/int 文字列化の影響を受ける。
- LN の chart string は文字連結ではなく `(int)lnChar + audioDurationMs` 相当。
- BMSON note は音声 slice の start/duration が charthash に効く。
- chart string は差分調査時に `tools/chartstring-dump` で Java 側を出して、C# の internal `ChartInfoParseResult.ChartString` と行単位比較する。

`charthash` は最も差分が残りやすい。
DataGrid field 拡張用の metadata としては、まず `notes`, `density`, `length`, `difficulty`, `BPM` などの表示値の互換を優先し、`charthash` は段階的に詰める方針が現実的。

## BMSON JSON parse

BMSON は `DataContractJsonSerializer` ではなく Json.NET ベースの tolerant reader を使う。

理由:

- beatoraja / Jackson は duplicate key を fatal にしない。
- `DataContractJsonSerializer` は duplicate member で `SerializationException` になる。
- 実 DB では duplicate key を含む BMSON でも beatoraja 側に値が入っている。

方針:

- invalid JSON は fatal。
- unknown field は無視。
- duplicate key は last-win。
- missing object / array は既定値で補う。

BMSON root default:

- `info = new`
- `lines = []`
- `bpm_events = []`
- `stop_events = []`
- `scroll_events = []`
- `sound_channels = []`
- `mine_channels = []`
- `key_channels = []`
- `bga = new`

BMSON info default:

- `mode_hint = "beat-7k"`
- `judge_rank = 100`
- `total = 100`
- `resolution = 240`

scroll event default:

- `rate = 1.0`

### BMSON int / nullable values

- `info.level` missing / explicit `null` は `chart_info.level = NULL`。
- `info.level = 0` は明示 0 として保存する。
- int 系は四捨五入しない。float / string numeric を受ける場合も int へ切り詰める。
- BMSON には BMS の `#DIFFICULTY` 相当の明示 field が無いため、`difficulty` は常に推定値、`difficulty_defined=false`。

### BMSON event merge order

BMSON timeline は y 順に merge する。
同一 y では参照実装に合わせて:

1. scroll
2. bpm
3. stop

stop の時刻計算には、その時点で作られた timeline の BPM を使う。
negative BPM / STOP などは fatal にせず diagnostic 扱いにする。

### BMSON scroll は後続 timeline に継承されない

BMSON の `getTimeLine(y, resolution)` は、直前 timeline から BPM と precise time は引き継ぐが、scroll は引き継がない。

参照実装では新規 `TimeLine` 作成時に概ね次の処理だけを行う:

```java
TimeLine tl = new TimeLine(y / resolution, (long) time, model.getMode().key);
tl.setBPM(bpm);
```

`TimeLine.scroll` の初期値は `1.0` なので、`scroll_events` はその y の timeline にだけ直接設定される。
C# 側で `previous.Scroll` をコピーすると、scroll 変化が後続 note / BGA / bar line timeline まで残り、`speedchange` が大きくずれる。

## difficulty / level

### level

`chart_info.level` は nullable。

- BMS `#PLAYLEVEL` missing / invalid: `NULL`
- BMS `#PLAYLEVEL 12`: `12`
- BMS `#PLAYLEVEL 12abc`: `NULL`
- BMS `#PLAYLEVEL ４`: `4`
- BMSON `info.level` missing / null: `NULL`
- BMSON `info.level = 0`: `0`

`level_defined` は持たない。
未定義は `NULL` だけで表現する。

### difficulty

`chart_info.difficulty` は DataGrid 表示・ソート用の有効値。
`difficulty_defined` は譜面に明示されていたかを表す。

BMS:

- `#DIFFICULTY 4`: `difficulty=4`, `difficulty_defined=true`
- `#DIFFICULTY 0`: 推定値, `difficulty_defined=false`
- missing / invalid: 推定値, `difficulty_defined=false`

BMSON:

- 常に推定値, `difficulty_defined=false`

推定順:

1. subtitle の `beginner/normal/hyper/another/insane/leggendaria`
2. title + subtitle
3. notes 数:
   - `<250`: 1
   - `<600`: 2
   - `<1000`: 3
   - `<2000`: 4
   - `>=2000`: 5

DataGrid では `difficulty` を表示・ソートに使い、`difficulty_defined=false` を警告表示に使う想定。

## fatal / recoverable / diagnostic

### fatal WARN 対象

逐次 WARN に出すのは fatal のみ。

- file read failure
- invalid BMSON JSON
- BMS / BMSON の最終 parse failure
- parser unexpected exception
- parse timeout

parse failure でも bytes から digest 計算できている場合:

- `chart_digest_map` は保存する。
- `chart_info` は成功行のみ保存する。

### diagnostic 扱い

参照実装の `DecodeLog.WARNING` 相当は基本的に diagnostic 扱い。

例:

- malformed known command
- undefined BPM / STOP reference
- invalid numeric token
- channel data invalid token
- LN conflict
- mine conflict

ログ肥大化を避けるため、通常は WARN 出力しない。

## backfill / timeout / commit

chart_info backfill は「単一 file reader + in-memory parallel parse + chunk commit」。

- file IO は reader 1 本。
- `byte[]` を bounded queue に積む。
- worker は `byte[]` から digest と chart_info を生成する。
- DB commit は writer 側で chunk 単位に行う。
- per-chart timeout は backfill 限定で 60 秒。
- timeout は parse failure として扱う。
- parser version は結果意味が変わる時だけ上げる。

ログで見るべき境界:

- `chart_info_backfill start`
- `parse_done`
- `slow_parse_top`
- `db_commit_chunk_start`
- `db_commit_chunk_done`
- summary の `timeoutFailed`, `parseMaxMs`, `dbCommitMaxChunkMs`

これにより「解析で止まったのか」「DB commit で止まったのか」を切り分ける。

2026-04-27 時点の full backfill 実測では、約 20.9 万譜面を次の状態で完走している。

- `total=209781`
- `success=209757`
- `failed=24`
- `timeoutFailed=0`
- `parseAvgMs=16`
- `parseP95Ms=33`
- `parseMaxMs=55529`
- `totalMs=909536`

以前 timeout していた巨大譜面も 60 秒 timeout 内で成功しており、production DB compare でも non-RANDOM の値差分は 0 になっている。
現時点では `chart_info` 生成基盤としては実用上問題ない状態とみなす。

今後さらに全体時間を詰める場合は、parser の平均値よりも次の long tail / commit 側を優先して見る。

- `slow_parse_top` 上位の巨大・特殊譜面
- `dbCommitMaxChunkMs`
- full backfill 中の DB chunk commit のばらつき

## production diff fixture の読み方

`BeMusicSeeker.Tests/TestData/chart_info_production_diff/` は本番 DB 差分を fixture 化したもの。

比較方針:

- beatoraja `song ∩ information` を参照母集団とする。
- RANDOM (`feature & 4 != 0`) は値差分許容。
- BMSON は beatoraja が md5 を保持しないため、BMSON の md5 差分は比較対象外。
- `level` は beatoraja `0` と app `NULL` を未定義相当として一致扱い。
- `maxbpm/minbpm` は整数部比較。
- double 系は epsilon 比較。

現時点で重要な見方:

- `core` 差分は `notes/n/ln/s/ls/lanenotes/difficulty/level/mode/judge/feature` などの表示値に直結するため最優先。
- `length`, `distribution`, `density`, `speedchange` は timeline time 差分で連動しやすい。
- `charthash` は chart string 文字列化・BMSON audio slice・LN duration など広い範囲に影響されるため、最後に詰める。

`BeMusicSeeker.Tests/TestData/chart_info_production_latest_diff/` は、最新 production DB compare で新しく観測された少数差分だけを追加保持するための補助 fixture。
既存の `chart_info_production_diff` に既に含まれる譜面は重複コピーしない。
2026-04-26 の latest report では non-RANDOM diff/missing が 7 件あり、そのうち 5 件は既存 fixture に含まれていたため、この補助 fixture には新規 2 件だけを入れている。

この 2 件の charthash-only 差分は、`tools/chartstring-dump` を JDK 17 で実行すると旧 C# parser の `ChartString` / `charthash` と一致する。
しかし songdata-updater / beatoraja 同梱 JRE は JDK 21 であり、`D:\beatoraja\jre\bin\java.exe` で同じ dump を実行すると production DB の期待値と一致する。
したがって production DB 側の生成経路差ではなく、JDK 17 と JDK 21 の `Double.toString(double)` 文字列表現差分として扱う。

確認済み JDK 17 dump:

- `f007405d0555d5e0919a4a1ff3ad7981cb8f7f47c505039905e2be2ca7b56d7c`: `e95b3b4976aa6f3b48cf726f1b490e4a4bf5a39e2010a165b9d1996df40ed8e9`
- `4ca15478b4e2aeb23e70fac38ce8dd2782bc5a9b082b76b04f54d34ebfd5f7f4`: `3d04846a93d36a87828546b478b4181a76b6f917a9cb446b7c247dab85ff5054`

確認済み JDK 21 dump / production DB reference:

- `f007405d0555d5e0919a4a1ff3ad7981cb8f7f47c505039905e2be2ca7b56d7c`: `8480a10763dfb91f44dfa895b545b429f2cc6f6490458a3e6d3368c81a7cd547`
- `4ca15478b4e2aeb23e70fac38ce8dd2782bc5a9b082b76b04f54d34ebfd5f7f4`: `745102a87afc4d87705b8b34d4501e57ea85977e497f9d476e6c4b13ee4134c1`

代表的な chart string 差分:

- JDK 17: `TOTAL:1.14514191981036442E18`
- JDK 21: `TOTAL:1.1451419198103644E18`
- JDK 17: `TOTAL:8.4929057819839846E17`
- JDK 21: `TOTAL:8.492905781983985E17`

## 現在の到達点と残論点

2026-04-27 時点で、production DB compare では非 RANDOM の比較対象について:

- core 差分は 0。
- `length`, `distribution`, `density`, `peakdensity`, `enddensity` は 0。
- `speedchange` / `charthash` も 0。
- timeout は 0。
- `chart_info` backfill は約 15 分で完走。

ここまでで DataGrid field 拡張用の metadata 基盤としては一旦区切りを付ける。
今後の主タスクは、作成済み `chart_info` を DataGrid でどのように表示・ソート・警告表示するかの設計と実装に移る。

今後の改善候補:

1. DataGrid での `chart_info` 表示設計
   - `LEVEL`, `DIFFICULTY`, `JUDGE`, `FEATURE`, `TOTAL`, `T/N`, `DENSITY`, `PEAK`, `END`, `LONG`, `SCRATCH`, `SPEEDCHANGE` など
   - `difficulty_defined=false` / `total_defined=false` の警告表示
   - 既存 LR2 `song` 由来値と `chart_info` 由来値の優先順位
2. keyword search field 拡張
   - `chart_info` 由来 field の命名
   - numeric range / undefined handling
   - feature bit flag の検索構文
3. RANDOM 譜面の deterministic branch 選択と beatoraja DB 期待値の扱い整理
   - 値差分は仕様上許容しているが、UI 表示上の説明が必要になる可能性がある
4. production DB 再生成時の差分追跡を容易にする report / fixture 更新手順の整備
5. `#SWITCH/#CASE/#SKIP/#ENDSW` の追加サンプルが出た場合の conditional stack 再確認
6. さらなる性能 tuning
   - `slow_parse_top` 上位譜面の個別 hotspot
   - chunk commit の最大遅延
