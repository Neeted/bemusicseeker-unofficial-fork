# BASS.NET から ManagedBass への移行計画

## 文書情報

| 項目 | 値 |
| --- | --- |
| Status | Ready for autonomous execution |
| 調査日 | 2026-08-06 |
| 計画作成時の branch / HEAD | `refactor` / `5efc151437387845af6d63ae91ff593a31ae50a4` |
| 移行先 | ManagedBass `4.0.2` stable |
| 対象 runtime | .NET 10 / C# 14 / WPF / Windows x64 |
| Native BASS | 現行 6 DLL の version、hash、配置を変更しない |
| 実行方針 | ユーザー承認待ちを挟まず、unit ごとに計画、実装、検証、レビュー、local commit を完了して次へ進む |
| Git baseline | 移行開始時点は tracked / untracked の差分と staged change がなく、各 unit は前 unit の commit 直後の clean worktree から開始する |

計画作成時の branch / HEAD は調査時点の情報であり、実行時の基準を固定するものではない。移行実行時は、計画書を含む準備変更がすべて commit 済みで、tracked / untracked の差分と staged change がない clean worktree を開始条件とする。実行者は開始時と各 unit の開始前に `git status --short --branch`、`git rev-parse HEAD`、適用範囲内の `AGENTS.md` を再確認する。

## Summary

BeMusicSeeker の managed BASS wrapper を、proprietary な BASS.NET (`Un4seen.Bass` / `Bass.Net.dll`) から MIT License の ManagedBass へ移行する。

結論として、現時点では ManagedBass が最も妥当な移行先である。理由は次のとおり。

- ManagedBass 自体が MIT License であり、本アプリの MIT License と自然に共存できる。
- BASS core、BASSmix、BASS_FX、BASSenc、BASSASIO、BASSWASAPI に対応する package が揃っている。
- 現行 BASS native DLL を置き換えず、managed P/Invoke binding だけを移行できる。
- BASS.NET の wrapper registration key を source tree と配布物から除去できる。
- 2026-08-06 時点の stable は `4.0.2` で、.NET 8 / .NET Standard 2.0 target を通じて .NET 10 と互換性がある。

ただし、この移行は namespace と method name の機械的置換では完了しない。特に BASS.NET の `Un4seen.Bass.Misc.BaseEncoder`、`EncoderWAV`、`EncoderLAME`、`EncoderNeroAAC`、`EncoderOPUS`、`EncoderFLAC`、`EncoderOGG`、`TAG_INFO` に相当する高水準 helper は ManagedBass に存在しない。ManagedBass.Enc の低水準 `BassEnc.EncodeStart` / `EncodeStop` を使い、encoder command line、metadata、lifecycle を project-owned code として再構成する必要がある。

また、ManagedBass は wrapper のみを MIT 化する。`bass.dll` と各 add-on DLL の license は引き続き Un4seen Developments の条件に従い、本アプリの MIT License へ取り込まれない。BASS native の配布・商用利用条件は本移行の対象外であり、既存の third-party notice を維持する。

## 調査結果と候補比較

### 推奨候補

| 候補 | License / 公開性 | 現行機能との適合 | 移行・保守コスト | 判断 |
| --- | --- | --- | --- | --- |
| ManagedBass `4.0.2` | MIT、source available | core / Mix / Fx / Enc / ASIO / WASAPI を提供。BASS.NET `Misc` encoder helper のみ project 側で補う必要がある | 中～高。API 名、enum、delegate、effect parameter、encoder helper を移行 | **採用** |
| 必要 API だけの project-owned P/Invoke | 本アプリの MIT code として実装可能 | 必要な native signature を正確に実装すれば完全適合 | 高。6 module、callback lifetime、marshalling、platform ABI、version tracking を継続保守 | 単一 API 欠落時の限定 fallback のみ |
| BASS.NET を継続 | proprietary。wrapper registration と個人 key が必要 | 現行実装そのもの | 短期は最小、open-source 化と key 管理の阻害が継続 | 不採用 |
| `Bass.NetWrapper` NuGet | 古い BASS.NET package であり独立候補ではない | 現行と同系統 | 古く、license 問題も解消しない | 不採用 |
| NAudio、CSCore、その他の audio engine | 各 library の license は別途評価可能 | BASS の add-on、backend negotiation、mixer、effect、encoder contract をそのまま提供しない | BASS 自体の置換になり非常に高い | 今回は対象外 |

NuGet と GitHub の調査では、現行が使う core / Mix / Fx / Enc / ASIO / WASAPI を一式カバーし、ManagedBass と同程度に利用可能な、別の active な MIT 系 .NET wrapper は確認できなかった。したがって、ManagedBass を本線とし、ManagedBass `4.0.2` に本アプリが必要とする単一 native entry point が欠けている場合だけ、公式 BASS header の signature に基づく最小 P/Invoke を project 内へ追加する。

### Primary references

実装時は package の compile result と `4.0.2` tag の source を正本にする。API documentation と current web page は補助情報として扱う。

- ManagedBass repository: <https://github.com/ManagedBass/ManagedBass>
- ManagedBass `4.0.2` license: <https://github.com/ManagedBass/ManagedBass/blob/4.0.2/LICENSE.md>
- ManagedBass `4.0.2` package: <https://www.nuget.org/packages/ManagedBass/4.0.2>
- ManagedBass API: <https://managedbass.github.io/api/ManagedBass.html>
- ManagedBass.Enc API: <https://managedbass.github.io/api/ManagedBass.Enc.html>
- BASS.NET registration / license: <https://www.radio42.com/bass/bass_register.html>
- BASS.NET purchase / license: <https://www.radio42.com/bass/bass_purchase.html>
- BASS native license: <https://www.un4seen.com/bass.html>

## 現行 repository の確認結果

### 依存関係

- `Directory.Packages.props`
  - `Un4seen.Bass` = `2.4.18.2`
- `BeMusicSeeker.csproj`
  - `PackageReference Include="Un4seen.Bass"`
- `BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj`
  - `PackageReference Include="Un4seen.Bass"`
- `packages.lock.json`
- `BeMusicSeeker.Tests/packages.lock.json`
- managed output: `Bass.Net.dll`
- native output directory: `libs/x64`

### 固定されている native dependency set

この表は `devdocs/spec/bass-runtime-dependency-set.md` の現行値であり、今回変更しない。

| Component | Native file | Exact version |
| --- | --- | --- |
| BASS core | `bass.dll` | `2.4.18.3` / packed `0x02041203` |
| BASSmix | `bassmix.dll` | `2.4.12.0` / packed `0x02040C00` |
| BASSenc | `bassenc.dll` | `2.4.17.0` / packed `0x02041100` |
| BASSWASAPI | `basswasapi.dll` | `2.4.4.1` / packed `0x02040401` |
| BASS_FX | `bass_fx.dll` | `2.4.12.6` / packed `0x02040C06` |
| BASSASIO | `bassasio.dll` | `1.4.3.0` / packed `0x01040300` |

`vendor/native/x64`、`libs/x64`、hash、PE machine、copy/publish rule は変更しない。6 DLL は常に同一の rollback unit とする。

### 現行 wrapper registration

`Ribbit/Media/Audio/BassNet.cs` は単なる registration shim ではない。次の責務を持つ。

- native load
- BASS.NET registration
- native version validation
- audio operation admission gate
- callback admission gate
- session initialization / cleanup gate
- shutdown sequencing
- unconfirmed native cleanup の quarantine

同ファイル内には BASS.NET の registration email / key を復元して `Un4seen.Bass.BassNet.Registration` へ渡す処理が存在する。値はこの計画書、log、test name、commit message、review prompt へ転記しない。

移行では lifecycle と gate を削除しない。wrapper-neutral な `BassAudioRuntime` へ改名し、BASS.NET registration stage だけを最終 unit で除去する。

### Native load boundary

`Ribbit/Media/Audio/BassNativeRuntime.cs` は `libs/x64` から 6 DLL を明示的に `LoadLibrary` し、handle を保持して reverse order で `FreeLibrary` している。現在は BASS.NET 経由の version API で exact version を検証する。

ManagedBass は各 assembly の `DllImport` 名として `bass`、`bassmix`、`bass_fx`、`bassenc`、`bassasio`、`basswasapi` を使用する。既存の exact native file ownership を維持するため、default probing へ依存せず、各 ManagedBass assembly へ `NativeLibrary.SetDllImportResolver` を一度だけ設定し、`BassNativeRuntime` が保持する exact handle を返す。

### BASS.NET 使用領域

#### Runtime / backend / mixer

- `Ribbit/Media/Audio/BassNativeRuntime.cs`
- `Ribbit/Media/Audio/BassNet.cs`
- `Ribbit/Media/Audio/BassAudioSession.cs`
- `Ribbit/Media/Audio/BassAudioBackendNegotiation.cs`
- `Ribbit/Media/Audio/BassAsioNegotiator.cs`
- `Ribbit/Media/Audio/BassWasapiNegotiator.cs`
- `Ribbit/Media/Audio/BassDirectSoundNegotiator.cs`
- `Ribbit/Media/Audio/BassAudioDeviceEnumerator.cs`
- `Ribbit/Media/Audio/BassMixerSourceController.cs`

#### Player / graph / effect

- `Ribbit/Media/BassAudioPlayer.cs`
  - BASS core stream and channel API
  - BASSmix
  - BASS_FX tempo
  - BASS core DX8 effect
  - BASS_FX effect parameter objects
  - WASAPI / ASIO callbacks
  - file callbacks and sync callbacks
  - mixer source ownership and natural-end cleanup

#### Encoder / metadata

- `Ribbit/Media/BassAudioWriter.cs`
- `Ribbit/BMS/BMSAutoPlayWriter.cs`
- `Ribbit/Media/Audio/EncodeTypeExt.cs`
- `Ribbit/Media/Audio/EncoderType.cs`

現行 encoder type と executable は次のとおり。

| EncoderType | Output | Current helper | External executable |
| --- | --- | --- | --- |
| `WAVE` | WAV | `EncoderWAV` | なし |
| `MP3_LAME` | MP3 | `EncoderLAME` | `lame.exe` |
| `AAC_NERO` | AAC/M4A | `EncoderNeroAAC` | `neroAacEnc.exe` |
| `OPUS` | Opus | `EncoderOPUS` | `opusenc.exe` |
| `FLAC` | FLAC | `EncoderFLAC` | `flac.exe` |
| `OGG_VORBIS` | Ogg Vorbis | `EncoderOGG` | `oggenc2.exe` |

#### Workflow / shutdown

- `BeMusicSeeker/ViewModels/AudioDeviceTestWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs`

#### Tests / deployment policy

- `BeMusicSeeker.Tests/AudioContractsTests.cs`
- `BeMusicSeeker.Tests/AudioDeviceTestWorkflowOwnerTests.cs`
- `BeMusicSeeker.Tests/AudioSettingsTestDoubles.cs`
- `BeMusicSeeker.Tests/BassAsioNegotiationTests.cs`
- `BeMusicSeeker.Tests/BassAudioSessionTests.cs`
- `BeMusicSeeker.Tests/BassMixerSourceControllerTests.cs`
- `BeMusicSeeker.Tests/BassNativeRuntimeTests.cs`
- `BeMusicSeeker.Tests/BassWasapiAndDirectSoundNegotiationTests.cs`
- `BeMusicSeeker.Tests/SettingDialogEditCompletionTests.cs`
- `BeMusicSeeker.Tests/ManagedDependencyOutputPolicyTests.cs`
- `BeMusicSeeker.Tests/UpdaterDeploymentBoundaryTests.cs`
- `BeMusicSeeker.Tests/UpdaterPackageSyncTests.cs`

Updater の legacy cleanup list に含まれる `Bass.Net.dll` は、旧 installation から obsolete file を削除するための互換性 contract である。最終 tree の BASS.NET dependency scan では、この明示的な legacy cleanup entry だけを許可し、削除しない。

## Goals

- BASS.NET package、assembly、namespace、registration call、registration material を current source / build / publish output から除去する。
- ManagedBass `4.0.2` stable の package setへ移行する。
- 現行 BASS native 6 DLL の version、hash、layout、load ownership を維持する。
- `devdocs/spec/audio-runtime-phase1.md` に定義された backend、fallback、ownership、shutdown、error、volume、device-test contract を維持する。
- DirectSound、WASAPI shared、WASAPI exclusive、ASIO、NullDevice conversion の observable behavior を維持する。
- mixer source ownership、generation-based natural-end cleanup、callback lifetime、unconfirmed cleanup quarantine を維持する。
- 6 encoder format、quality mapping、tag、collision suffix、command-line diagnostics、pull-driven encoding を維持する。
- persisted enum numeric value と setting name を変更しない。
- license / third-party notice を正しい境界へ更新する。
- reviewable unit ごとに local commit を作り、migration 完了まで承認待ちで停止しない。

## Non-goals

- BASS native library または add-on の置換。
- native BASS version、hash、archive、DLL layout の更新。
- audio backend fallback matrix の再設計。
- persisted `EncoderType`、`SampleRate`、`SampleFormat`、device setting の変更。
- 新しい encoder format の追加または既存 format の廃止。
- app-wide audio abstraction framework の新設。
- cross-platform audio 対応の拡張。
- Git history の rewrite、force-push、key revocation の vendor 手続き。
- release、version bump、tag、push、GitHub Release 公開。

## Closed decision list

以下はこの計画で確定済みとし、実装中に再度ユーザー判断を求めない。

1. **ManagedBass version**
   - stable `4.0.2` を exact pin する。
   - `4.1.0-prerelease` は使用しない。
   - migration 中に新しい stable が公開されても version を変更しない。更新は別 change とする。
2. **Package set**
   - `ManagedBass`
   - `ManagedBass.Mix`
   - `ManagedBass.Fx`
   - `ManagedBass.Enc`
   - `ManagedBass.Asio`
   - `ManagedBass.Wasapi`
   - `ManagedBass.Tags` は追加しない。現行 `TAG_INFO` は BASS.NET の managed DTO としてしか使われていない。
3. **Native dependency set**
   - 現行 6 DLL と exact version/hash/layout を変更しない。
4. **Runtime owner**
   - `BassNet` は `BassAudioRuntime` へ改名する。
   - operation gate、callback gate、session lifecycle、shutdown、cleanup quarantine は維持する。
5. **Error model**
   - BASS-specific internal boundary は `ManagedBass.Errors` を使用してよい。
   - public/persisted app enum を ManagedBass enum へ置換しない。
   - broad wrapper facade は追加せず、既存の narrow native boundary を維持する。
6. **Effects**
   - 現行 `FxParameterTypeToBASSFXType` が表す DX8 / BASS_FX effect set を狭めない。
   - 実際の current call が volume / peak EQ に偏っていても、既存 public/internal API が受け付ける全 mapping を ManagedBass parameter type へ移行する。
7. **Encoder**
   - BASS.NET `Misc.BaseEncoder` 系を project-owned `AudioEncoderSession` と `AudioEncoderCommandFactory` へ置換する。
   - BASSenc の `BassEnc.EncodeStart` / `EncodeStop` を使用する。
   - shell を経由せず、BASSenc が直接 executable を起動する command line を生成する。
   - current format、quality、tag、output collision、lifecycle を golden behavior とする。
8. **Security**
   - registration email/key の値を表示、decode、log、test fixture 化しない。
   - current source からは完全に削除する。
   - Git history rewrite は本 migration では行わない。
   - key は既に history に存在するため compromised と扱い、公開前の revocation/rotation と history publication 方針を non-blocking release prerequisite として記録する。
9. **Updater compatibility**
   - `Bass.Net.dll` を publish output から除去する。
   - legacy updater cleanup entry は旧 installation cleanup のため残す。
10. **Commit / remote operation**
    - 本文書は、この migration scope に限る local commit の明示承認として扱う。
    - unit ごとに承認を求めず commit する。
    - push、tag、release、version bump、history rewrite は行わない。
11. **Missing ManagedBass API fallback**
    - `4.0.2` に必要な単一 overload / entry point がない場合、公式 BASS header と native documentation に合わせた最小 project-owned P/Invoke を追加する。
    - wrapper 全体を自作しない。
    - prerelease package へ逃げない。
12. **Decision conflict resolution**
    - observable behavior が不明な場合は、次の優先順で決める。
      1. `devdocs/spec` の現行 contract
      2. migration 前に固定した golden characterization
      3. existing behavioral test
      4. current production code の成功時 / 失敗時 behavior
      5. native BASS official contract
      6. compatibility を最大化し、新機能を追加しない選択
    - この順で一意に決め、判断と根拠を plan / decision record へ追記し、ユーザー承認待ちにしない。

## Codex autonomous execution contract

### 中断しない実行ルール

- migration 開始後、Unit 0 から Completion Gate までを一つの連続した作業として扱う。
- unit 間で「次へ進めてよいか」「この方針でよいか」をユーザーへ質問しない。
- 中間の進捗通知を出す場合も、それを応答終了点または承認 gate にしない。
- unit が完了したら、plan の `Progress` と `Verification Log` を更新し、local commit を作成し、直ちに次 unit へ進む。
- 最終応答は Completion Gate をすべて評価した後に一度だけ行う。
- 失敗を回避するために scope を黙って縮小しない。introduced failure は修正し、pre-existing / out-of-scope の build・test failure は証拠を記録して targeted acceptance を完了する。

### Repository hygiene

本 migration は、開始時点に未コミット差分が一切ない clean worktree を前提とする。計画書を含む準備変更は migration 開始前に commit 済みでなければならない。

Unit 0 の開始前に次を実行する。

```powershell
git status --short --branch
git status --porcelain=v1 --untracked-files=all
git rev-parse HEAD
git diff --check
git diff --cached --check
```

- `git status --porcelain=v1 --untracked-files=all` の出力は空であること。tracked / untracked / staged の既存変更はない。
- branch、開始 HEAD、clean baseline の確認結果を `Verification Log` に記録する。
- 日本語ファイル名、改行コード正規化、file mode 変更に起因する既存差分はないものとする。既存差分を分離するための特殊な staging や別 worktree は不要であり、使用しない。
- 各 unit は前 unit の local commit 直後から開始する。unit 開始時の `git status --porcelain=v1 --untracked-files=all` は空でなければならない。
- worktree の内容を巻き戻す目的で `reset`、`checkout`、`restore`、`stash`、`clean` を使わない。mass-format や repository-wide line-ending conversion も行わない。
- unit scope 外の file、timestamp、line ending、file mode を変更しない。
- migration 中に生じる変更はすべて当該 unit の変更として扱い、`git add -- <explicit paths>` で明示的に stage する。`git add .` と `git add -A` は使わない。
- package restore による lock file 更新は、clean baseline から生成された当該 unit の planned delta として full-file stage してよい。手編集はせず、restore の再実行で再現できることを確認する。
- commit 前に次を確認する。

```powershell
git status --short --branch
git diff --name-status
git diff --cached --name-status
git diff --cached --check
git diff --cached
```

- staged diff に unit scope 外の file が一つでもあれば stage をやり直し、planned paths だけに絞る。
- unstaged diff が残る場合は、その unit の planned file の stage 漏れか、意図しない変更である。原因を解消してから commit する。
- commit 後に次を実行し、worktree と index が再び clean であることを確認してから次 unit へ進む。

```powershell
git status --porcelain=v1 --untracked-files=all
git show --stat --oneline --decorate --no-renames HEAD
```

`git status --porcelain=v1 --untracked-files=all` の出力は空でなければならない。

### Planner / reviewer loop

コード、設定、script を変更する各 reviewable unit で `AGENTS.md` に従う。

1. ルート agent が unit の目的、対象範囲、対象外、acceptance、compatibility、closed decisions、開始時の clean baseline、current unit delta を整理する。
2. `.codex/agents/unit-planner.toml` の planner を呼ぶ。
3. planner 実行中は repository を凍結する。
4. planner が repo reality と異なる場合は差異だけを返して再計画する。
5. planner が `NEEDS_DECISION` を返した場合、上記 Closed decision list と conflict resolution で compatibility-preserving decision を確定し、plan / decision record へ追記して planner を再実行する。ユーザーへ質問しない。
6. 実装と標準検証後、`.codex/agents/repo-static-review.toml` の reviewer を呼ぶ。
7. P0 / P1 と acceptance に直接反する P2 を修正し、影響範囲を再検証し、fresh reviewer を呼ぶ。
8. 同じ unit で二回の修正 review 後も新しい P1 が続く場合、unit 内で ownership / scope を再分割し、planner を再実行する。migration 全体を止めない。
9. planner / reviewer が利用不能な場合は、同じ read-only contract の generic subagent を使う。利用可能な subagent 自体がない場合は、root agent がその事実、代替した static checklist、実行結果を `Verification Log` に明記し、作業を継続する。

### Commit policy

想定 commit は次の順。planner が reviewability のため一つをさらに分割する場合は、同じ prefix と scope を保ち、plan に実際の commit を記録する。

1. `test(audio): characterize BASS.NET migration behavior`
2. `refactor(audio): add ManagedBass runtime bootstrap`
3. `refactor(audio): migrate audio backends to ManagedBass`
4. `refactor(audio): migrate audio player effects to ManagedBass`
5. `refactor(audio): replace BASS.NET encoder helpers`
6. `chore(deps): retire BASS.NET`

各 commit は buildable / testable な review snapshot にする。BASS.NET と ManagedBass が一時的に同居することは許可するが、最終 commit までに BASS.NET dependency を完全撤去する。

## Target dependency and output state

### Central package versions

`Directory.Packages.props` に exact version を一度だけ定義する。

```xml
<PackageVersion Include="ManagedBass" Version="4.0.2" />
<PackageVersion Include="ManagedBass.Mix" Version="4.0.2" />
<PackageVersion Include="ManagedBass.Fx" Version="4.0.2" />
<PackageVersion Include="ManagedBass.Enc" Version="4.0.2" />
<PackageVersion Include="ManagedBass.Asio" Version="4.0.2" />
<PackageVersion Include="ManagedBass.Wasapi" Version="4.0.2" />
```

Final state では `Un4seen.Bass` の central version と project reference を削除する。

### Managed output

Final build / publish output は少なくとも次の managed assemblies を含み、`Bass.Net.dll` を含まない。

- `ManagedBass.dll`
- `ManagedBass.Mix.dll`
- `ManagedBass.Fx.dll`
- `ManagedBass.Enc.dll`
- `ManagedBass.Asio.dll`
- `ManagedBass.Wasapi.dll`

実際の package output と `.deps.json` を build artifact から列挙し、test の expected list を package reality に合わせる。case、file name、package id を推測だけで固定しない。

### Native output

次の file は現行と同じ path、version、hash で残す。

- `libs/x64/bass.dll`
- `libs/x64/bassmix.dll`
- `libs/x64/bass_fx.dll`
- `libs/x64/bassenc.dll`
- `libs/x64/bassasio.dll`
- `libs/x64/basswasapi.dll`

## Target runtime design

### `BassAudioRuntime`

`Ribbit/Media/Audio/BassNet.cs` を `BassAudioRuntime.cs` へ rename し、wrapper 名ではなく application lifecycle owner として表現する。

維持する責務:

- one-time runtime initialization
- operation / callback admission
- session initialization serialization
- session cleanup registration
- shutdown sequencing
- callback drain
- native cleanup quarantine
- native unload ownership

最終的に削除する責務:

- BASS.NET wrapper registration
- registration email/key reconstruction
- `WrapperRegistration` stage
- `BASS.NET` 固有 log component name

final bootstrap sequence:

1. `BassNativeRuntime.Load()`
2. ManagedBass resolver installation / handle publication
3. exact native version validation through ManagedBass
4. runtime admission open

resolver installation は native P/Invoke より前に一度だけ行う。resolver callback は current handle table を参照し、shutdown 後の stale handle を返さない。再初期化 test がある場合、resolver 自体は一度、handle table は generation ごとに更新する。

### `ManagedBassNativeLibraryResolver`

新規 helper の想定責務:

- ManagedBass assembly 六つへ `NativeLibrary.SetDllImportResolver` を設定する。
- library name を extension / case 非依存で canonicalize する。
- `bass` -> `bass.dll`
- `bassmix` -> `bassmix.dll`
- `bass_fx` -> `bass_fx.dll`
- `bassenc` -> `bassenc.dll`
- `bassasio` -> `bassasio.dll`
- `basswasapi` -> `basswasapi.dll`
- unknown library name は `IntPtr.Zero` を返し、default resolution を許す。
- known BASS library が未ロードなら `IntPtr.Zero` で黙って別 copy を探さず、caller が deterministic failure を得る設計を優先する。実際の resolver contract 上 default fallback になる場合は、known-but-unavailable を先に `DllNotFoundException` として fail させる wrapper を設ける。
- resolver に渡す handle の ownership は `BassNativeRuntime` に残し、resolver が free しない。

### Version validation

ManagedBass `4.0.2` の `Bass.Version` と各 add-on `Version` は `System.Version` を返す。現行 packed value と比較するため、次のいずれか一つへ統一する。

- required constant を `System.Version` に変換して exact 4-part compare する。
- packed `uint` を維持し、`Version` を deterministic に pack する helper を作る。

既存 test と diagnostics が packed value を必要とする場合は後者を使う。major / minor / build / revision の順序、leading zero、revision 未指定時を unit test で固定する。

### Narrow native boundaries

既存の `IAudioSessionNativeBoundary`、backend-specific boundary、mixer source boundary を維持し、method body と enum/delegate 型だけを ManagedBass へ移行する。全 BASS API を覆う新しい巨大 interface は追加しない。

### Callback lifetime

ManagedBass が一部 core callback を内部保持するとしても、application の static / session-owned delegate field は削除しない。少なくとも次を native lifetime 中に strong reference で保持する。

- `WasapiProcedure`
- `AsioProcedure`
- `SyncProcedure`
- `FileProcedures` と各 file callback
- encoder notify / data callback を追加する場合の delegate

shutdown では callback admission を閉じ、callback drain を待ち、session/native handle を解放し、その後に native DLL を unload する現行順序を維持する。

## Principal API mapping

これは migration の初期 map であり、compile された ManagedBass `4.0.2` API と tag source を正本に確定する。

| BASS.NET | ManagedBass `4.0.2` |
| --- | --- |
| `BASSError` | `Errors` |
| `Bass.BASS_ErrorGetCode()` | `Bass.LastError` |
| `BASSFlag` | `BassFlags` |
| `BASSInit` | `DeviceInitFlags` |
| `BASSConfig` | `Configuration` または対応 property |
| `BASSAttribute` | `ChannelAttribute` |
| `BASSFXType` | `EffectType` |
| `BASS_DEVICEINFO` | `DeviceInfo` |
| `BASS_INFO` | `BassInfo` |
| `BASSLevel` | ManagedBass level return type / explicit left-right conversion |
| `BASS_FILEPROCS` | `FileProcedures` |
| `SYNCPROC` | `SyncProcedure` |
| `Bass.BASS_Init(...)` | `Bass.Init(...)` |
| `Bass.BASS_Free()` | `Bass.Free()` |
| `Bass.BASS_GetDevice()` | `Bass.CurrentDevice` または `Bass.GetDevice` equivalent |
| `Bass.BASS_SetDevice(...)` | `Bass.CurrentDevice = ...` または `Bass.SetDevice` equivalent |
| `Bass.BASS_StreamCreateFile(...)` | `Bass.CreateStream(...)` |
| `Bass.BASS_StreamCreateFileUser(...)` | `Bass.CreateStream(StreamSystem, BassFlags, FileProcedures, ...)` |
| `Bass.BASS_StreamCreate(...)` | `Bass.CreateStream(...)` |
| `Bass.BASS_StreamFree(...)` | `Bass.StreamFree(...)` |
| `Bass.BASS_ChannelGetData(...)` | `Bass.ChannelGetData(...)` |
| `Bass.BASS_ChannelSetPosition(...)` | `Bass.ChannelSetPosition(...)` |
| `Bass.BASS_ChannelGetPosition(...)` | `Bass.ChannelGetPosition(...)` |
| `Bass.BASS_ChannelSetAttribute(...)` | `Bass.ChannelSetAttribute(...)` |
| `Bass.BASS_ChannelGetAttribute(...)` | `Bass.ChannelGetAttribute(...)` |
| `Bass.BASS_ChannelSetSync(...)` | `Bass.ChannelSetSync(...)` |
| `Bass.BASS_ChannelRemoveSync(...)` | `Bass.ChannelRemoveSync(...)` |
| `Bass.BASS_ChannelSetFX(...)` | `Bass.ChannelSetFX(...)` |
| `Bass.BASS_FXSetParameters(...)` | `Bass.FXSetParameters(handle, IEffectParameter)` |
| `Bass.BASS_FXGetParameters(...)` | `Bass.FXGetParameters(handle, IEffectParameter)` |
| `BassMix.BASS_Mixer_StreamCreate(...)` | `BassMix.CreateMixerStream(...)` |
| `BassMix.BASS_Mixer_StreamAddChannel(...)` | `BassMix.MixerAddChannel(...)` |
| `BassMix.BASS_Mixer_ChannelGetMixer(...)` | `BassMix.ChannelGetMixer(...)` |
| `BassMix.BASS_Mixer_ChannelFlags(...)` | `BassMix.ChannelFlags(...)` |
| `BassMix.BASS_Mixer_ChannelRemove(...)` | `BassMix.MixerRemoveChannel(...)` または `ChannelRemove` equivalent を tag source で確定 |
| `BassFx.BASS_FX_TempoCreate(...)` | `BassFx.TempoCreate(...)` |
| `BASSASIOFormat` | `AsioSampleFormat` |
| `ASIOPROC` | `AsioProcedure` |
| `BassAsio.BASS_ASIO_ErrorGetCode()` | `BassAsio.LastError` |
| `BassAsio.BASS_ASIO_Init(...)` | `BassAsio.Init(...)` |
| `BassAsio.BASS_ASIO_SetDevice(...)` | `BassAsio.CurrentDevice = ...` |
| `BassAsio.BASS_ASIO_SetRate(...)` | `BassAsio.Rate = ...` または `SetRate` equivalent |
| `BassAsio.BASS_ASIO_ChannelEnable(...)` | `BassAsio.ChannelEnable(...)` |
| `BassAsio.BASS_ASIO_ChannelJoin(...)` | `BassAsio.ChannelJoin(...)` |
| `BassAsio.BASS_ASIO_ChannelSetFormat(...)` | `BassAsio.ChannelSetFormat(...)` |
| `BASSWASAPIFormat` | `WasapiFormat` |
| `BASSWASAPIInit` | `WasapiInitFlags` |
| `WASAPIPROC` | `WasapiProcedure` |
| `BassWasapi.BASS_WASAPI_Init(...)` | `BassWasapi.Init(...)` |
| `BassWasapi.BASS_WASAPI_GetInfo(...)` | `BassWasapi.GetInfo(...)` |
| `BassWasapi.BASS_WASAPI_SetDevice(...)` | `BassWasapi.CurrentDevice = ...` または equivalent |
| `BassEnc.BASS_Encode_Start(...)` | `BassEnc.EncodeStart(...)` |
| `BassEnc.BASS_Encode_Stop(...)` | `BassEnc.EncodeStop(...)` |

Overload の意味が BASS.NET と異なる箇所では compile を通すことだけで完了扱いにしない。特に WASAPI shared / exclusive、file stream ownership、automatic delegate reference、mixer pause flag、effect parameter marshalling、encoder header flag を behavioral test で確認する。

## Encoder target design

### `AudioTagInfo`

BASS.NET `TAG_INFO` を project-owned immutable model に置換する。現行 `BMSAutoPlayWriter` が設定する field を少なくとも保持する。

- artist
- title
- genre
- duration
- bpm
- file name
- comment

field name / casing は domain naming へ改善してよいが、output metadata の値と欠落時 behavior を変えない。value は null と empty を current behavior に合わせて canonicalize する。

### `AudioEncoderCommandFactory`

責務:

- encoder type、binary path、output path、sample rate、channel count、sample format、quality、tag を immutable request として受ける。
- deterministic な command line と `EncodeFlags` を返す。
- Windows argument quoting を一箇所に閉じ込める。
- executable、output path、metadata に space、quote、non-ASCII、trailing backslash が含まれる test を持つ。
- `cmd.exe` / PowerShell / shell redirection を使わない。
- overwrite prompt を起こさない option または事前 output delete を current behavior に合わせて選ぶ。
- diagnostics へ返す command line は secret を含めない。

### `AudioEncoderSession`

責務:

- source channel / mixer handle
- encoder handle
- output path
- encoder type
- command line
- start / stop state
- notify callback lifetime
- error code / stage
- disposal ownership

state transition:

1. `Created`
2. `Started` only after non-zero `BassEnc.EncodeStart` handle
3. `Stopping`
4. `Stopped` only after successful or deterministically finalized `EncodeStop`
5. `Faulted` with original native error / process failure
6. `Disposed`

二重 start / stop、start failure、encoder process early death、pull loop failure、shutdown 中 cleanup を test する。start failure で started state を publish しない。stop failure で original failure を隠さない。

### Format preservation matrix

Unit 0 で BASS.NET helper の command line / property behavior を characterization し、Unit 3 の ManagedBass implementation が同じ observable result を満たすようにする。

| Format | Current quality behavior | Migration acceptance |
| --- | --- | --- |
| WAV | quality parameter なし | BASSenc PCM/WAV output。sample format、header、extension、tag behavior を preserve |
| LAME MP3 | quality clamp `0.1..1.0`、VBR、accurate ReplayGain、quality `10 - (int)(10*q)` | current command option、output path、metadata、VBR quality を golden match |
| Nero AAC | quality clamp `0..1`、quality mode | current input format、quality option、output extension、metadata behavior を golden match |
| Opus | bitrate `int(6 + 250*q)` | bitrate calculation、channel/sample-rate option、metadata を golden match |
| FLAC | quality clamp `0..0.8`、ReplayGain、compression `int(10*q)` | compression、ReplayGain、metadata、output collision を golden match |
| Ogg Vorbis | quality clamp `0..0.8`、quality `int(10*q)` | quality mode、metadata、output collision を golden match |

BASS.NET helper を test-only で安全に instantiate できる場合は、実 executable を起動せず `EncoderCommandLine` を golden 化する。constructor が native operation を要求し安全に characterize できない場合は、次の順で fallback する。

1. current source property mapping を testable project-owned expectation に固定する。
2. dummy executable / fake process fixture を使える format は end-to-end command capture を行う。
3. small deterministic PCM input と available encoder binary を使う opt-in smoke test を追加する。
4. user approval は求めず、どの level で固定したかを Verification Log に記録する。

### Pull-driven encoding

現行 `BassAudioWriter` は decoder/mixer から `Bass.ChannelGetData` で data を pull し、その channel に付いた BASSenc DSP が encoder へ sample を送る。これを維持する。

- `EncodeStart` は pull loop より前に成功させる。
- source channel と encoder handle の ownership を同じ session が持つ。
- `ChannelGetData` の end / error / partial result を区別する。
- encoder が premature exit した場合は `EncodeIsActive` / notify / error を使って failure として返す。
- stop と native graph cleanup の順序を test する。

## Compatibility invariants

### Persisted values

次を変更しない。

- `EncoderType` の numeric value
- `SampleRate` の numeric value
- `SampleFormat` の numeric value
- audio backend / device setting key
- selected encoder setting key
- quality setting interpretation
- output file namingと `name (2)` 形式の collision suffix

migration 前後で enum numeric values を比較する architecture test を追加する。

### Audio runtime

`devdocs/spec/audio-runtime-phase1.md` を acceptance の正本とする。特に次を壊さない。

- WASAPI shared / exclusive の別 boundary
- shared mode で exclusive / autoformat flag を混入しないこと
- ASIO sample-rate / format negotiation
- Float32 decode mixer
- callback source publication order
- mixer volume effect と desired managed volume state
- NullDevice conversion gain と mute independence
- requested / negotiated / observed values の区別
- stable device identity と hotplug handling
- device-test startup pre-roll、1 秒 measurement、ratio `0.75..1.25`、natural end
- mixer source attach readback と `BASS_ERROR_ALREADY` race handling
- generation-based end sync cleanup
- cleanup quarantine と shutdown gate

### Logging

- production logging は `Ribbit/Logging/NLogWrapper.cs` を経由する。
- component label は `BASS.NET` ではなく `BASS Runtime` または `ManagedBass` とする。
- native function stage は診断互換性のため `BASS_Init`、`BASS_WASAPI_Init` 等を維持してよい。
- wrapper registration stage は final state で存在しない。
- registration material、user path の不要部分、metadata private value を log しない。

## Progress

各 unit の完了時に Status、commit、review、verification、残課題を更新する。`Not started` の unit を飛ばさない。

| Unit | Status | Commit | Progress Notes |
| --- | --- | --- | --- |
| Unit 0: Characterization and dependency foundation | Not started |  |  |
| Unit 1: ManagedBass native bootstrap and runtime owner | Not started |  |  |
| Unit 2A: Backend, session, device and mixer migration | Not started |  |  |
| Unit 2B: Player, stream, callback and effect migration | Not started |  |  |
| Unit 3: Encoder and metadata migration | Not started |  |  |
| Unit 4: BASS.NET retirement, compliance and publish acceptance | Not started |  |  |

## Unit 0: Characterization and dependency foundation

### Intent

BASS.NET をまだ production route として維持したまま、移行後に守る observable behavior を test で固定し、ManagedBass `4.0.2` package set を同居させる。

### Planned paths

- `Directory.Packages.props`
- `BeMusicSeeker.csproj`
- `BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj`
- `packages.lock.json`
- `BeMusicSeeker.Tests/packages.lock.json`
- `BeMusicSeeker.Tests/*BassNetMigrationCharacterizationTests.cs` または責務別 test file
- `BeMusicSeeker.Tests/*AudioEncoder*Tests.cs`
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. current status と dependency graph を capture する。
2. six ManagedBass package versions を central package management に exact pin する。
3. app project へ six package reference を追加する。
4. test project へ test source が直接使う package reference を明示する。不要な direct reference は final cleanup で削除してよいが、lock graph は deterministic にする。
5. locked restore を意識して lock files を正規の `dotnet restore` で更新する。手編集しない。
6. ManagedBass package metadata、license、assembly names、TFM selection を restore artifact から記録する。
7. 次の characterization test を追加する。
   - persisted enum numeric values
   - encoder quality clamp / mapping
   - output extension
   - output collision suffix
   - encoder binary search order
   - BASS.NET encoder command line per format、可能な範囲の tag option
   - `EncoderCommandLine` exposure timing
   - start / stop state and error contract that can be observed without a physical device
   - version pack/unpack semantics
   - effect parameter type map count / supported set
   - updater legacy `Bass.Net.dll` cleanup entry の目的を固定する test
8. characterization test は BASS.NET registration material を読まない、復元しない、snapshot に含めない。
9. source-based test ではなく observable property / generated command / result を優先する。
10. plan の characterization result を更新する。

### Acceptance

- BASS.NET production behavior は変更されていない。
- ManagedBass six packages が exact `4.0.2` で restore される。
- lock files に floating range がない。
- characterization test が current implementation で通る。
- test artifact / log に registration material がない。
- existing `ManagedDependencyOutputPolicyTests` が新 package の一時同居を不当に拒否する場合、final output contract ではなく transition-aware assertion に限定して更新する。`Bass.Net.dll` removal assertion は Unit 4 まで有効化しない。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'BassNetMigrationCharacterizationTests|AudioEncoderCommandFactoryTests|AudioContractsTests|ManagedDependencyOutputPolicyTests|UpdaterDeploymentBoundaryTests|UpdaterPackageSyncTests'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

Dependency restore の異常を早期検出するため、必要に応じて次も実行する。

```powershell
dotnet restore .\BeMusicSeeker.sln --locked-mode
```

lock 更新直後の `--locked-mode` は更新済み lock が安定した後に実行する。

### Review focus

- golden test が implementation text に過度に結合していないか
- registration material を fixture 化していないか
- package version が exact か
- BASS.NET / ManagedBass coexistence による ambiguous type や unintended output policy change がないか

### Commit

`test(audio): characterize BASS.NET migration behavior`

## Unit 1: ManagedBass native bootstrap and runtime owner

### Intent

既存 native DLL ownership を維持したまま ManagedBass の P/Invoke を exact loaded handle へ結び付け、version validation を ManagedBass へ移す。lifecycle owner を wrapper-neutral に改名する。ただし、残る BASS.NET production call のため wrapper registration は一時的に維持する。

### Planned paths

- `Ribbit/Media/Audio/BassNativeRuntime.cs`
- `Ribbit/Media/Audio/BassNet.cs` -> `Ribbit/Media/Audio/BassAudioRuntime.cs`
- `Ribbit/Media/Audio/ManagedBassNativeLibraryResolver.cs`（新規）
- `Ribbit/Media/Audio/*Runtime*.cs` の call sites
- `BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs`
- `BeMusicSeeker.Tests/BassNativeRuntimeTests.cs`
- runtime / shutdown / operation gate tests
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. unit planner で resolver ownership、idempotence、unload order、test seams を確定する。
2. `ManagedBassNativeLibraryResolver` を追加する。
3. six ManagedBass assemblies へ resolver を一度だけ設定する。
4. `BassNativeRuntime.Load()` の sequence を次へ整理する。
   - path / file validation
   - native load
   - handle table publication
   - resolver availability
   - ManagedBass `Version` API による exact validation
5. version mismatch 時は current exception type、component name、expected/actual、stage を維持する。
6. partial load failure は reverse order で current generation の handle だけを free する。
7. resolver の static lifetime と native handle generation を分離する。
8. `BassNet` class/file を `BassAudioRuntime` へ rename し、全 call site を update する。
9. current BASS.NET registration method は `RegisterLegacyBassNetWrapper` のような temporary 明示名へ変更し、残る BASS.NET call より前に一度だけ呼ぶ。値の表現や難読化を変更しない。Unit 4 まで存在するため、diff / log へ値を展開しない。
10. initialize stage は temporary に `LegacyWrapperRegistration` を保持し、final removal marker を comment ではなく plan / test に置く。
11. runtime logger 名を wrapper-neutral にする。
12. tests を ManagedBass version property と resolver behavior へ更新する。

### Required tests

- resolver が六つの known name を exact handle へ map する。
- extension / casing variation が canonicalize される。
- unknown library name は intended fallback behavior になる。
- resolver installation は repeat call で二重登録例外を起こさない。
- load failure の handle rollback order。
- version mismatch の component / expected / actual。
- unload 後に stale handle を publish しない。
- callback / operation gate を閉じてから unload する。
- `BassAudioRuntime` rename 後も admission / shutdown behavior が同じ。
- BASS.NET registration が remaining legacy call より前に実行される transition test。

### Acceptance

- native file set、version、hash、path は不変。
- native version validation は ManagedBass API を使用する。
- BASS.NET version API は production からなくなる。
- lifecycle / gate test はすべて維持される。
- remaining BASS.NET code があるため registration はまだ削除しない。
- new default native probing が `libs/x64` の exact set を迂回しない。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'BassNativeRuntimeTests|BassAudioSessionTests|AudioContractsTests|ShellShutdownWorkflowOwnerTests'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

### Review focus

- `SetDllImportResolver` の assembly 単位 semantics
- handle ownership / reference count
- resolver callback と shutdown の race
- registration-before-legacy-call invariant
- unrelated audio behavior change が混入していないか

### Commit

`refactor(audio): add ManagedBass runtime bootstrap`

## Unit 2A: Backend, session, device and mixer migration

### Intent

DirectSound core、WASAPI、ASIO、session lifecycle、device enumeration、mixer source boundary を ManagedBass API へ移す。`BassAudioPlayer` の大規模 stream/effect migration は Unit 2B へ残し、narrow boundary と fake を先に安定させる。

### Planned paths

- `Ribbit/Media/Audio/BassAudioBackendNegotiation.cs`
- `Ribbit/Media/Audio/BassAudioSession.cs`
- `Ribbit/Media/Audio/BassAsioNegotiator.cs`
- `Ribbit/Media/Audio/BassWasapiNegotiator.cs`
- `Ribbit/Media/Audio/BassDirectSoundNegotiator.cs`
- `Ribbit/Media/Audio/BassAudioDeviceEnumerator.cs`
- `Ribbit/Media/Audio/BassMixerSourceController.cs`
- `BeMusicSeeker/ViewModels/AudioDeviceTestWorkflowOwner.cs`
- corresponding tests and fakes
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. BASS.NET enum / struct / delegate を ManagedBass equivalent へ mapping する。
2. native error retrieval を次へ統一する。
   - core / mix / fx / enc: `Bass.LastError`
   - ASIO: `BassAsio.LastError`
   - WASAPI: ManagedBass API が expose する `LastError` または native contract に対応する core error source
3. existing typed exception / result の stage、source、code、requested/negotiated values を維持する。
4. `BASSError` property を `Errors` へ変更し、serialization / UI text / log の observable value を確認する。
5. ASIO:
   - `CurrentDevice`
   - `Init`
   - `Rate`
   - `CheckRate`
   - `ChannelEnable`
   - `ChannelJoin`
   - `ChannelSetRate`
   - `ChannelSetFormat`
   - `ChannelGetFormat` / info equivalent
   - `ChannelSetVolume`
   - `Start` / `Stop` / `Free`
6. WASAPI:
   - device enumeration
   - shared and exclusive init overload/flags
   - info readback
   - current device context
   - callback start / stop / free
   - event-driven fallback contract
7. DirectSound/core:
   - device enumeration
   - init / free / current device
   - no-sound device and mixer creation contract
8. mixer source controller:
   - `CreateMixerStream`
   - `MixerAddChannel`
   - `ChannelGetMixer`
   - pause flag transitions via `ChannelFlags`
   - remove and readback
   - `Errors.Already` race handling
9. test fakes は wrapper method spelling ではなく boundary behavior を表す名前へ改善してよい。
10. persisted app enum を ManagedBass enum の numeric value へ alias しない。explicit mapping function を使用する。

### Acceptance

- backend selection / fallback matrix は不変。
- WASAPI shared が exclusive / autoformat path を通らない。
- ASIO format/rate negotiation と fallback reason は不変。
- device catalog identity、default、unavailable saved item の behavior は不変。
- mixer attach / pause / resume / remove の readback contract は不変。
- Unit 2A 対象 file から `Un4seen.Bass` namespace がなくなる。
- BASS.NET registration は writer/player の残存 call のためまだ維持する。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'BassAsioNegotiationTests|BassWasapiAndDirectSoundNegotiationTests|BassAudioSessionTests|BassMixerSourceControllerTests|AudioDeviceTestWorkflowOwnerTests|AudioSettingsTestDoubles|SettingDialogEditCompletionTests|AudioContractsTests'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

### Review focus

- ManagedBass property setter が exception を投げる API と bool-return API の failure contract 差
- thread-local current device context
- shared / exclusive overload selection
- enum bit flag mapping
- fake が wrapper-specific behavior を誤って隠していないか
- cleanup failure が primary failure を上書きしていないか

### Commit

`refactor(audio): migrate audio backends to ManagedBass`

## Unit 2B: Player, stream, callback and effect migration

### Intent

`BassAudioPlayer` と直接関連する workflow/test を ManagedBass へ移し、stream、tempo、mixer graph、callback、sync、effect の現行 contract を維持する。

### Planned paths

- `Ribbit/Media/BassAudioPlayer.cs`
- `BeMusicSeeker/ViewModels/AudioDeviceTestWorkflowOwner.cs`
- `BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs`
- player / playback / mixer / device-test tests
- effect-specific tests
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. file / memory / user-callback stream creation overload を ManagedBass へ移す。
2. `FileProcedures` callback の signature と lifetime を current behavior に合わせる。
3. position、length、seconds/bytes、data pull、level、attribute、play、sync の call を移す。
4. BASSmix graph creation / source ownership は Unit 2A boundary を通す。
5. tempo stream を `BassFx.TempoCreate` へ移す。
6. effect type / parameter map を ManagedBass `EffectType` と `IEffectParameter` 実装へ置換する。
7. current DX8 / BASS_FX mapping 全体を inventory test で固定し、同じ count / semantic set を実装する。
8. `Bass.FXSetParameters(handle, IEffectParameter)` / `FXGetParameters` を使い、不要な manual pinning は追加しない。
9. volume effect と peak EQ の current initialization / update order を維持する。
10. WASAPI / ASIO callback delegate を ManagedBass signature へ移し、static strong reference を維持する。
11. callback は `BassAudioSession.CallbackOutputHandle` だけを読み、static mixer field を source-of-truth にしない。
12. end sync:
    - one-time flag
    - generation id
    - pending cleanup atomic state
    - natural-end position
    - dispose/play race
    を既存 test どおり維持する。
13. stream create / attach / resume / rollback failure の typed diagnostic を維持する。
14. Unit 0 characterization と既存 test を ManagedBass implementation へ向ける。

### Effect migration rule

現行 map にある次の category を削除しない。

- DX8 chorus / compressor / distortion / echo / flanger / gargle / I3DL2 reverb / param EQ / reverb
- BASS_FX rotate / echo variants / flanger / volume / peak EQ / reverb / LPF / mix / damp / auto-wah / phaser / chorus / APF / compressor variants / volume envelope / biquad filter / pitch shift / freeverb

ManagedBass `4.0.2` で type name や class hierarchy が異なる場合、tag source の `EffectType` と effect parameter object を照合する。単一 parameter type が欠けている場合は、その effect だけの official native struct と minimal P/Invoke path を実装する。全 effect API を独自再実装しない。

### Acceptance

- `BassAudioPlayer.cs` から `Un4seen.Bass` がなくなる。
- current playback, pause, stop, seek, tempo, EQ, volume, mute behavior が test で同じ。
- callback delegate の GC collection による crash risk がない。
- mixer source ownership / voice count / natural end / cleanup quarantine が維持される。
- effect supported set を狭めない。
- writer 以外の production BASS.NET call がなくなる。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'AudioContractsTests|BassAudioSessionTests|BassMixerSourceControllerTests|BassAsioNegotiationTests|BassWasapiAndDirectSoundNegotiationTests|AudioDeviceTestWorkflowOwnerTests|SettingDialogEditCompletionTests|BassAudioPlayerTests|AudioPlayback|SelectedChartAudioConversion'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

Filter 名は実在 test class / trait に合わせて planner が確定し、空 hit の filter を成功扱いにしない。実行された test count を Verification Log に記録する。

### Review focus

- overload mismatch と flag semantics
- `AutoFree` / `FxFreeSource` による ownership 移動
- ManagedBass が delegate を内部保持する API と app-owned delegate の二重前提
- `IEffectParameter` type / layout
- callback exception containment
- generation cleanup race
- `CurrentVoices` の増減が native readback と一致するか

### Commit

`refactor(audio): migrate audio player effects to ManagedBass`

## Unit 3: Encoder and metadata migration

### Intent

BASS.NET `Misc` helper と `TAG_INFO` を project-owned model/lifecycle へ置換し、six output format を ManagedBass.Enc で維持する。

### Planned paths

- `Ribbit/Media/BassAudioWriter.cs`
- `Ribbit/BMS/BMSAutoPlayWriter.cs`
- `Ribbit/Media/Audio/EncodeTypeExt.cs`
- `Ribbit/Media/Audio/EncoderType.cs`（numeric value は不変）
- `Ribbit/Media/Audio/AudioTagInfo.cs`（新規）
- `Ribbit/Media/Audio/AudioEncoderCommandFactory.cs`（新規）
- `Ribbit/Media/Audio/AudioEncoderSession.cs`（新規）
- encoder / conversion workflow tests
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. Unit 0 golden を再確認し、format ごとの command / quality / tag / extension matrix を plan に確定する。
2. immutable `AudioTagInfo` を追加し、`BMSAutoPlayWriter` を移す。
3. command request/result model と safe Windows quoting helper を追加する。
4. six format の command generation を実装する。
5. WAV は BASSenc PCM/WAV mode を使用する。current WAV metadata が存在する場合、`EncodeAddChunk` 等で RIFF INFO を再現する。current behavior が metadata 無しなら新規追加しない。
6. external encoder format は executable path と output path を quote し、STDIN input と overwrite behavior を current helper と合わせる。
7. metadata option の escape / encoding を format ごとに実装する。unsupported field は黙って別 field へ詰めず、current behavior に合わせて omit する。
8. `AudioEncoderSession` を実装し、non-zero handle のみ start success とする。
9. `BassAudioWriter.EncoderCommandLine`、`Encoder`、output path、start/stop public behavior を維持する。
10. pull-driven render loop と encoder process status を統合する。
11. encoder early exit、missing executable、invalid output path、disk/write error、native error を typed failure へ変換する。
12. `StopRecording` は encoder stop と graph cleanup の順序を current contract に合わせる。
13. BASS.NET helper に依存する characterization test は、ManagedBass implementation の wrapper-neutral golden test へ置換する。移行後も価値がある quality/quoting/tag/collision test は残す。
14. production / test から `BaseEncoder`、`EncoderWAV`、`EncoderLAME`、`EncoderNeroAAC`、`EncoderOPUS`、`EncoderFLAC`、`EncoderOGG`、`TAG_INFO` を除去する。
15. source tree を scan し、BASS.NET API call が temporary registration 以外にないことを確認する。

### Required tests

- all six command factories
- quality min / typical / max / out-of-range clamp
- executable and output path quoting
- metadata with space、quote、Unicode、empty/null
- output collision `name (2)` / repeated collisions
- missing encoder binary
- start success / zero-handle failure
- double start / double stop
- encoder premature death
- pull loop end / native data error
- cleanup when conversion workflow is cancelled or faults
- `EncoderType` numeric values unchanged
- `EncoderCommandLine` lifecycle and diagnostics
- available executable を使う opt-in format smoke test
- WAV deterministic header / duration smoke test without physical device

### Acceptance

- six format と current quality semantics が維持される。
- `BMSAutoPlayWriter` から BASS.NET DTO がなくなる。
- `BassAudioWriter` から BASS.NET `Misc` がなくなる。
- source tree の BASS.NET call は final removal 待ちの registration method だけ。
- command injection を生む shell usage がない。
- registration material を command/logへ混入しない。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'AudioEncoderCommandFactoryTests|AudioEncoderSessionTests|BassAudioWriterTests|BMSAutoPlayWriterTests|SelectedChartAudioConversionWorkflowOwnerTests|AudioContractsTests'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

available tool を要求する smoke test は通常 Functional に含めず opt-in category とし、availability 判定と skip reason を deterministic にする。

### Review focus

- argument quoting / injection
- encoder option parity
- metadata loss / encoding
- state transition と handle ownership
- failure時の partial output handling
- output collision race
- BASSenc auto-feed と pull loop の順序

### Commit

`refactor(audio): replace BASS.NET encoder helpers`

## Unit 4: BASS.NET retirement, compliance and publish acceptance

### Intent

temporary coexistence を終了し、BASS.NET dependency、registration、secret material、managed output、license notice を current tree から撤去する。spec、dependency policy、updater compatibility、publish artifact を final state へ揃える。

### Planned paths

- `Directory.Packages.props`
- `BeMusicSeeker.csproj`
- `BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj`
- `packages.lock.json`
- `BeMusicSeeker.Tests/packages.lock.json`
- `Ribbit/Media/Audio/BassAudioRuntime.cs`
- remaining source / tests with legacy namespace or stage
- `BeMusicSeeker.Tests/ManagedDependencyOutputPolicyTests.cs`
- `BeMusicSeeker.Tests/UpdaterDeploymentBoundaryTests.cs`
- `BeMusicSeeker.Tests/UpdaterPackageSyncTests.cs`
- `devdocs/spec/bass-runtime-dependency-set.md`
- `devdocs/spec/audio-runtime-phase1.md`
- `devdocs/decisions/managedbass-adoption.md`（新規、重複を避けて短い ADR）
- `ThirdPartyNotices.txt`
- localized third-party notice counterpart が存在する場合その file
- `third_party/licenses/02-BASS.NET-NOTICE.txt`
- `third_party/licenses/*ManagedBass*`（新規 MIT notice/license）
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`

### Steps

1. all tracked source/test/docs/output policy を scan し、remaining BASS.NET use を分類する。
2. `BassAudioRuntime` から次を削除する。
   - registration reconstruction data
   - registration method call
   - legacy wrapper registration stage
   - BASS.NET-specific log text
3. bootstrap を `native load -> ManagedBass resolver/version validation -> admission open` へ確定する。
4. `Un4seen.Bass` package reference と central version を削除する。
5. restore して lock files から `Un4seen.Bass` を削除する。
6. output policy test を final ManagedBass assembly set へ更新する。
7. `Bass.Net.dll` が app build / publish / updater payload に入らないことを assert する。
8. updater legacy cleanup list の `Bass.Net.dll` は残し、test で「旧 file の削除対象」として説明する。
9. `devdocs/spec/bass-runtime-dependency-set.md` を更新する。
   - managed wrapper = ManagedBass `4.0.2`
   - six managed packages / assemblies
   - native six-DLL set は unchanged
   - resolver / output contract
10. `devdocs/spec/audio-runtime-phase1.md` を更新する。
    - bootstrap から BASS.NET registration を削除
    - wrapper 名を ManagedBass / wrapper-neutral runtime に更新
    - API spelling ではなく behavior contract を優先
11. `devdocs/decisions/managedbass-adoption.md` を追加する。
    - context
    - ManagedBass adopted
    - alternatives
    - BASS native license remains separate
    - BASS.NET registration history is sensitive
12. `ThirdPartyNotices.txt` から current-distribution BASS.NET section を削除し、ManagedBass MIT notice を追加する。
13. BASS.NET notice file は current distribution dependency がゼロになったことを確認して削除する。historical reference が必要なら `devdocs/decisions` に vendor license text を複製せず、Git history 参照だけを記す。
14. ManagedBass MIT license/notice を `third_party/licenses` に追加する。upstream license text を正確に保持する。
15. BASS native notices は維持し、ManagedBass MIT と混同しない。
16. security scan を実行する。registration value を terminal に表示せず、pattern の hit count / file path だけを出す helper を使う。
17. full build / test / publish / updater acceptance を実行する。
18. final plan Progress、Verification Log、Completion Gate を更新する。
19. clean baseline からの staged diff を explicit path で作り、review 後に final local commit を作る。

### Final scans

通常の text scan:

```powershell
rg -n --hidden --glob '!\.git/**' --glob '!artifacts/**' --glob '!bin/**' --glob '!obj/**' 'Un4seen\.Bass|BassNet\.Registration|BASSNET_EMAIL|BASSNET_REGKEY|Bass\.Net\.dll' .
```

Expected:

- `Un4seen.Bass`: zero current source/config/test/dependency hit
- `BassNet.Registration`: zero
- `BASSNET_EMAIL` / `BASSNET_REGKEY`: zero
- `Bass.Net.dll`: only approved updater legacy cleanup assertions / migration history references

登録情報の難読化断片については exact value を出力しない専用 scan を行う。Unit 4 開始時に legacy method の source span を hash / token class として capture し、削除後はその identifier、array name、reconstruction helper、unique non-secret marker が zero であることを確認する。key 本体を再構成して比較しない。

Build/publish artifact scan:

```powershell
Get-ChildItem -Recurse .\bin, .\artifacts\publish -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(Bass\.Net|Un4seen\.Bass).*' } |
    Select-Object FullName
```

Expected: zero.

Managed assembly check:

```powershell
Get-ChildItem -Recurse .\bin\x64\Release, .\artifacts\publish -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'ManagedBass*.dll' } |
    Select-Object Name, FullName
```

Expected: final package set の assembly が build / publish contract に従って存在する。

### Acceptance

- package graph / lock files に `Un4seen.Bass` がない。
- current source / tests に BASS.NET namespace / API がない。
- registration material と reconstruction code がない。
- `Bass.Net.dll` が build / publish / updater payload にない。
- updater legacy cleanup entry だけは残る。
- ManagedBass license notice があり、BASS native license notice が維持される。
- native six DLL の version/hash/layout が計画開始時と同じ。
- specs が final implementation と一致する。
- Functional 180 秒 budget を満たす。
- Full lane が pass する、または pre-existing unrelated build・test failure が evidence と targeted acceptance で明確に分離される。introduced failure は一つも残さない。
- final static reviewer に blocking finding がない。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'ManagedDependencyOutputPolicyTests|UpdaterDeploymentBoundaryTests|UpdaterPackageSyncTests|BassNativeRuntimeTests|AudioContractsTests|AudioEncoderCommandFactoryTests|AudioEncoderSessionTests|BassAudioWriterTests'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

加えて:

```powershell
git diff --check
git status --short --branch
```

### Review focus

- registration material の完全撤去
- package / lock / output / notices の一貫性
- updater legacy cleanup entry の誤削除
- BASS native license の誤って MIT 扱い
- spec と実装の divergence
- publish artifact の stale `Bass.Net.dll`
- unit scope 外の source / generated file の accidental stage

### Commit

`chore(deps): retire BASS.NET`

## Verification strategy

### Per-unit Quick filters

実在 test 名へ合わせて調整し、0 test run を pass として記録しない。

| Area | Minimum tests |
| --- | --- |
| Native bootstrap | `BassNativeRuntimeTests`, runtime admission/shutdown tests |
| Session/backend | `BassAudioSessionTests`, `BassAsioNegotiationTests`, `BassWasapiAndDirectSoundNegotiationTests` |
| Mixer/playback | `BassMixerSourceControllerTests`, `AudioContractsTests`, player/end-sync tests |
| Device workflow | `AudioDeviceTestWorkflowOwnerTests`, `SettingDialogEditCompletionTests` |
| Encoder | new command/session tests, writer/conversion workflow tests |
| Dependency/output | `ManagedDependencyOutputPolicyTests`, updater boundary/sync tests |

### Standard lanes

- development loop: filtered `Quick`
- each code unit before review: `Functional`
- dependency/publish retirement: `Full`
- reviewer fix: affected `Quick`; rerun `Functional` only if normal behavior premise changed; rerun `Full` only if release lane changed

Functional command 全体の 180 秒 budget を変更しない。timeout 時は process tree を止め、TRX / blame / console progress を `artifacts/verification` から調査し、timeout 延長だけで隠さない。

### No-device smoke tests

物理 device を必要とせず、少なくとも次を実行可能にする。

- core no-sound device init
- decode stream -> mixer -> PCM pull
- BASSenc WAV output from deterministic PCM/decode source
- native version validation
- resolver mapping
- session cleanup repetition
- encoder command generation

### Opt-in physical-device matrix

通常 Functional の blocking gate にはしないが、available machine では実行し、結果を plan へ記録する。user approval待ちにはしない。

- Windows 10 / 11
- WASAPI shared default endpoint
- WASAPI shared non-default endpoint
- WASAPI exclusive supported format
- WASAPI exclusive busy / unsupported format fallback
- ASIO available device、Float32 / Int16 negotiation
- volume / mute behavior
- device test natural end
- repeated init / free
- hotplug / unavailable saved device
- NullDevice conversion

machine に device がない場合は skip reason を記録し、unit/fake/no-device acceptance を完了する。device 不在を理由に production fallback matrix を変更しない。

## Risk register

| Risk | Detection | Required response |
| --- | --- | --- |
| ManagedBass DllImport が default probing で別 DLL を load | resolver test、loaded module path、version/hash validation | exact handle resolver を修正。native layoutを変更しない |
| `SetDllImportResolver` 二重設定 | repeated initialization test | assemblyごとに once guard。resolver tableだけ更新 |
| handle unload後に resolverがstale pointerを返す | unload/reload generation test | handle table publication/clearをatomic化 |
| BASS.NET registrationを早く削除しremaining callが例外 | transition scan/test | Unit 4までtemporary registrationを維持 |
| enum/flag numeric mismatch | mapping tests、negotiation tests | explicit mapping。castだけで済ませない |
| property setterがexceptionを投げfailure contractが変化 | negative-path test | boundaryでcatchしexisting typed failureへ変換 |
| callback delegateがGCされる | forced GC callback lifetime test where safe、static/session refs review | strong ownershipを維持 |
| mixer `AutoFree` / Fx `FreeSource` がownershipを移す | lifecycle/readback tests | current explicit ownershipを維持しflagを外す |
| effect parameter type/layout mismatch | full effect inventory、FX set/get smoke | correct ManagedBass objectまたはsingle minimal P/Invoke |
| WASAPI sharedがexclusive overloadへ流れる | boundary fake flag/overload assertion | separate shared/exclusive implementationを維持 |
| encoder command quoting/injection | pathological path/tag tests | one quoting implementation、no shell |
| metadataがformatごとに失われる | golden command/output metadata test | current observable tagsを再現 |
| encoder start failureでstateがStartedになる | zero-handle test | publish state only after success |
| encoder process early exitがsuccess扱い | notify/is-active/pull failure test | typed conversion failure |
| stale `Bass.Net.dll` がpublishに残る | Full output scan | `bin` / `obj` / publish artifact など生成物だけを再生成または削除し、tracked source は変更しない |
| updaterからlegacy cleanup entryを消す | updater test | explicit allowlistとして保持 |
| BASS nativeをMITと誤表記 | notice review | separate proprietary noticeを維持 |
| registration keyがGit historyに残る | security note | current treeから削除。公開前にownerがrevoke/rotateしfresh-historyまたは別途authorized rewriteを選ぶ |
| unit scope外のfileを誤ってcommit | clean baseline、explicit stage、cached diff review、post-commit clean check | stageをやり直し、planned pathsだけcommitする |

## Automatic fallback rules

実装中に想定外が出ても次の規則で継続する。

1. **ManagedBass overload 不足**
   - exact `4.0.2` tag sourceを確認する。
   - equivalent API compositionでcurrent behaviorを表せるなら使用する。
   - 表せなければそのnative entry pointだけproject-owned P/Invokeを追加する。
2. **BASS.NET helper behaviorがcharacterize不能**
   - current source property mapping、existing tests、native encoder contractの順でgoldenを固定する。
   - userに質問しない。
3. **physical device 不在**
   - fake / no-device smoke / opt-in skip evidenceで進める。
   - fallback matrixを変更しない。
4. **external encoder binary 不在**
   - command factoryとdummy process fixtureで通常testを完了する。
   - real binary smokeはopt-inとしてskip reasonを記録する。
5. **pre-existing unrelated test failure**
   - same commandのartifactとtargeted rerunで分離する。
   - migration testとintroduced areaをpassさせる。
   - unrelated fileを修正またはstageしない。ただしAGENTSのflaky/timeout policyに従い、原因と影響を記録する。
6. **new stable ManagedBass release**
   - migration中は採用しない。`4.0.2`を維持する。
7. **review finding がunit scopeを超える**
   - acceptanceに直接必要ならunitを再分割し同migration内で修正する。
   - unrelated recommendationは記録し、取り込まない。

## Security and open-source publication note

この migration により、current source tree、build、publish artifact から BASS.NET registration key を除去できる。一方、既に commit 済みの Git history から自動的に消えるわけではない。

migration の engineering completion と、repository を public にする release prerequisite を分離する。

- migration completion:
  - current tree / artifacts に key と BASS.NET dependency がない。
- public release prerequisite:
  - BASS.NET registration key を vendor 側で revoke / rotate する。
  - old history を公開しない fresh-history publication、または別途明示承認された history rewrite を選ぶ。
  - BASS native redistribution/license 条件を再確認する。

Codex は migration 中に key を decode / display せず、vendor account 操作、history rewrite、force-push を行わない。この external prerequisite は Unit 0～4 の進行を止めない。

## Verification Log

実行者が unit ごとに実際の command、duration、test count、result、artifact path、review finding、commit SHA を追記する。

| Unit | Check | Result | Duration / Count | Notes |
| --- | --- | --- | --- | --- |
| Planning | Repository inventory / license / API research | Passed | 2026-08-06 | ManagedBass `4.0.2` selected。BASS.NET `Misc` encoder gap identified。 |

## Completion Gate

次をすべて評価するまで migration 完了と報告しない。

### Dependency / source

- [ ] `Un4seen.Bass` package reference がゼロ。
- [ ] lock files に `Un4seen.Bass` がない。
- [ ] production / test source に `using Un4seen.Bass...` がない。
- [ ] BASS.NET API call がない。
- [ ] registration call / reconstruction code / non-secret unique marker がない。
- [ ] `BassNet` lifecycle class name が `BassAudioRuntime` へ移行済み。
- [ ] ManagedBass six packagesがexact `4.0.2`。
- [ ] `ManagedBass.Tags`を追加していない。

### Runtime behavior

- [ ] exact native six-DLL resolver が機能する。
- [ ] native version/hash/layoutがmigration前と同じ。
- [ ] DirectSound/core、WASAPI shared/exclusive、ASIO、NullDevice contractsが維持される。
- [ ] mixer ownership、callback lifetime、shutdown、cleanup quarantine testsがpass。
- [ ] effect supported setが狭まっていない。

### Encoder

- [ ] WAV / LAME / Nero AAC / Opus / FLAC / Ogg Vorbisが維持される。
- [ ] quality mapping、tag、extension、collision suffix、command diagnosticsがgolden match。
- [ ] start/stop/error/cleanup testsがpass。
- [ ] shell injection pathがない。

### Distribution / license

- [ ] build / publish outputに`Bass.Net.dll`がない。
- [ ] outputにexpected ManagedBass assembliesがある。
- [ ] updater legacy cleanup entryだけは残る。
- [ ] ManagedBass MIT noticeが追加済み。
- [ ] BASS.NET current-distribution noticeが撤去済み。
- [ ] BASS native noticeが維持されている。
- [ ] public release prerequisiteとしてhistory/key noteが記録されている。

### Verification / review / Git

- [ ] all related Quick tests pass。
- [ ] Functional command total <= 180 seconds and pass。
- [ ] Full lane pass、またはpre-existing unrelated build・test failureがevidence付きで分離され、introduced failureはゼロ。
- [ ] `git diff --check` pass。
- [ ] final static reviewにblocking findingなし。
- [ ] unitごとのlocal commitが存在する。
- [ ] Unit 0開始前のworktree / indexがcleanであったことをVerification Logに記録済み。
- [ ] 各unit commit後と最終時点のworktree / indexがclean。
- [ ] push / tag / release / history rewriteを実行していない。
- [ ] ProgressとVerification Logが実結果へ更新済み。

## Final response contents

Completion Gate 後の最終応答には次だけをまとめる。

- ManagedBass採用とexact version
- main architectural changes
- encoder helper replacement
- test / Functional / Full results
- commit list
- remaining external publication prerequisite（key revocation / history publication / BASS native license）
- pre-existing unrelated build・test failuresがあればその明確な分離

unit間の承認依頼、未実行の提案、次回作業への持ち越しを最終応答へ残さない。migration scope内の未完了がある場合は、応答を終えずCompletion Gateを満たすまで作業を継続する。
