# ManagedBass 移行後クローズアウト計画

- Status: Ready for Codex execution
- Reviewed repository HEAD: `0177922e2ccc9eafa7648653865b5afd9cd57fbd`
- Reviewed branch: `refactor`
- Review date: 2026-08-09
- Intended repository path: `devdocs/plan/managedbass-post-migration-closeout-plan.md`

## 1. 目的

BASS.NET から ManagedBass への実装移行を正式にクローズし、次の三点を完了させる。

1. 現行の非商用リリース方針に対して、ネイティブ BASS 関連コンポーネントを正しく `GREEN` として扱い、英日サードパーティ・ノーティス、ADR、README、手動受入文書の矛盾と誤帰属を解消する。
2. 元の移行計画に明記されていた「利用可能な外部エンコーダ実行ファイルを使う opt-in smoke test」を追加し、project-owned encoder command/session/writer が実プロセスでも成立することを確認する。
3. 完了済みの移行計画を `devdocs/plan` の active plan から退役させ、恒久的な仕様・判断・検証結果だけを `devdocs/spec` / `devdocs/decisions` に残す。

この作業は ManagedBass のバージョン、ネイティブ DLL のバージョン・ハッシュ、音声挙動、設定形式、配布形式を変更しない。

## 2. レビュー結論

### 2.1 移行完了として受け入れてよい範囲

コード、依存関係、ネイティブロード、バックエンド、エフェクト、エンコーダ抽象化、writer pull rendering の観点では、ManagedBass 移行は完了として扱ってよい。

確認できた主な証拠は次のとおり。

- `Directory.Packages.props` と app/test の lock file は、`ManagedBass`、`ManagedBass.Mix`、`ManagedBass.Fx`、`ManagedBass.Enc`、`ManagedBass.Asio`、`ManagedBass.Wasapi` をすべて exact `4.0.2` に固定している。
- current source/package graph から `Un4seen.Bass`、BASS.NET package、登録処理、登録素材が除去されている。updater が旧インストールの `Bass.Net.dll` を削除する互換処理だけは意図的に残っている。
- `BassNativeRuntime` と `ManagedBassNativeLibraryResolver` は、保持する六つの x64 DLL を明示ロードし、ManagedBass の各 P/Invoke 名を runtime-owned handle へ結び付け、CLR がキャッシュした関数ポインタの寿命を process lifetime まで安全に保持する。
- DirectSound / WASAPI / ASIO negotiation、mixer ownership、callback lifetime、32 種の effect ID と構造体 layout、encoder lifecycle、partial read、natural end、encoder early death、cleanup retry に対する behavior test が追加されている。
- ネイティブ DLL 六点の SHA-256 は `devdocs/spec/bass-runtime-dependency-set.md` と一致している。
- commit log は characterization、runtime bootstrap、backend/player、encoder、pull rendering、BASS.NET retirement を reviewable unit に分けている。
- 移行計画の verification log は Unit 4 Full lane について Functional 3,756 executed / 0 failed、process integration 35 passed / 1 skipped、release acceptance 2 passed、update receipt passed、Roslynator 0 diagnostics を記録し、final fresh static review は blocking finding なしとしている。

レビュー環境には .NET SDK 10.0.302、`dotnet`、`pwsh`、MSBuild が無かったため、今回のレビューでは build/test を独立再実行していない。上記 test 結果は repository に記録された実行証跡に基づく。今回独立に行ったのは、履歴・差分・source/tests/docs の static review、package lock の照合、native hash の照合、禁止参照 scan、`git diff --check` である。

### 2.2 クローズ前に修正する項目

#### F-1: サードパーティ・ノーティスの BASS 判定が内部矛盾している — release documentation blocker

`ThirdPartyNotices.txt` / `ThirdPartyNotices.ja.txt` の BASS 本文は `GREEN` のままだが、末尾 summary だけが「商用再配布権限を確認する必要がある」として `YELLOW` にしている。さらに `bass_fx.dll` と `bassasio.dll` を含む六つすべてを `un4seen developments` の単一条件へ誤ってまとめている。

履歴上、移行前の `YELLOW` は BASS.NET の取得 archive、正式な wrapper license、registration entitlement に対するものだった。commit `f57a4721` の review correction で、その bullet がネイティブ BASS の hypothetical commercial entitlement に置き換えられ、BASS を GREEN 一覧から外した。これは新しいライセンス問題が発見された結果ではなく、current non-commercial release と future commercial release を混同した保守的な文書変更である。

修正方針:

- current release の accepted fact は「upstream の non-commercial 条件を満たす非収益の end-user software」である。
- current release に商用ライセンスが無いことを `YELLOW` 理由にしない。
- 将来、sales、advertising、sponsorship、paid support、paid bundle その他 product から収益を得る方針へ変える場合だけ、policy-change trigger として commercial license review を再実施する。
- BASS core / official add-ons、BASSASIO、BASS_FX を別 entry にし、著作権者と条件を正しく記録する。

#### F-2: 元計画で必須だった real external encoder opt-in smoke が無い — acceptance-direct test gap

`AudioEncoderCommandFactoryTests`、`AudioEncoderSessionTests`、`BassAudioWriterTests`、conversion workflow tests は command、fake native boundary、failure/cleanup を十分に覆うが、実際の `lame.exe` / `neroAacEnc.exe` / `opusenc.exe` / `flac.exe` / `oggenc2.exe` のうち利用可能なものへ deterministic PCM を渡す test は存在しない。

元計画の「small deterministic PCM input と available encoder binary を使う opt-in smoke test」と、required tests の「available executable を使う opt-in format smoke test」を満たすため、通常 lane に外部 tool を必須化せず、明示 opt-in の process integration test を追加する。

#### F-3: 完了済み計画が active `devdocs/plan` に残っている — documentation lifecycle gap

`AGENTS.md` は `devdocs/plan` を未完了計画用とし、完了後は spec/decision へ統合して削除または履歴整理するよう定めている。`devdocs/plan/bassnet-to-managedbass-migration-plan.md` は全 Unit を `Passed` としているため、恒久情報を ADR/spec へ統合した後に active plan から退役させる。

### 2.3 移行完了を妨げない外部事項

過去の Git 履歴に BASS.NET registration material が含まれる可能性への vendor-side revocation/rotation または公開履歴の sanitization は、current tree の ManagedBass 移行とは別の security/release gate である。本クローズアウトで history rewrite、force-push、key reconstruction、key value の表示を行わない。

## 3. ライセンス判断

以下は技術的なコンプライアンス判断であり、法的助言ではない。Codex は user-approved release fact を再質問せず decision として扱う。

### 3.1 Accepted release fact

現行 BeMusicSeeker の配布は upstream の non-commercial 条件を満たす。すなわち、non-commercial entity による end-user software であり、sales、advertising 等によって product から収益を得ない。BASS/BASSASIO を development component として resale/sublicense しない。

### 3.2 Component classification

| Entry | 対象 | Current status | 根拠と記載方針 |
| --- | --- | --- | --- |
| BASS core + official add-ons | `bass.dll`, `bassmix.dll`, `bassenc.dll`, `basswasapi.dll` | GREEN | BASS は non-commercial entity が product から収益を得ない場合に無料。BASSmix、BASSenc、BASSWASAPI は BASS と共に無料で利用できる。commercial license は将来の monetization trigger として注記する。 |
| BASSASIO | `bassasio.dll` | GREEN | BASSASIO は BASS と別の license だが、同じく non-commercial entity / no revenue 条件で無料。current release は条件を満たす。 |
| BASS_FX | `bass_fx.dll` | GREEN | third-party add-on で、著作権者は `(: JOBnik! :) / Arthur Aminov`。作者は利用を無料とし、commercial/free software での利用を明示している。unmodified DLL と作者 notice を保持し、no-fee current distribution として別 entry にする。exact retained 2.4.12.6 archive/readme evidence を記録する。 |
| ManagedBass | managed package set 4.0.2 | GREEN | MIT。native BASS terms と分離する。 |

Primary sources to record:

- BASS: `https://www.un4seen.com/bass.html` and `https://www.un4seen.com/doc/bass/bass.html`
- BASSmix: `https://www.un4seen.com/doc/bassmix/bassmix.html`
- BASSenc: `https://www.un4seen.com/doc/bassenc/bassenc.html`
- BASSWASAPI: `https://www.un4seen.com/doc/basswasapi/basswasapi.html`
- BASSASIO: `https://www.un4seen.com/bassasio.html` and `https://www.un4seen.com/doc/bassasio/bassasio.html`
- BASS_FX listing: `https://www.un4seen.com/bass.html`
- BASS_FX author/disclaimer: `https://jobnik.net/projects/bass_fx/` and `https://jobnik.net/projects/bass_fx/disclamer/`

`YELLOW` は「primary source が確認できない」または「current release 条件が未決」のときだけ使う。将来 commercial 化する可能性だけで current release を `YELLOW` にしない。

## 4. Codex 自走契約

Codex はこの計画全体を一つの合意済み作業手順として扱う。

1. 開始時点の worktree は tracked / staged / untracked すべて clean とする。`git status --short --branch` が clean でない場合は、既存差分を reset/clean/stash せず、差分の存在を記録して安全に分離できないときだけ停止する。
2. `AGENTS.md` を読み、コード・設定・script の変更前に `.codex/agents/unit-planner.toml` の `unit-planner` を呼ぶ。planner 実行中は repository を変更しない。
3. Unit 1 から Unit 3 まで、調査、実装、filtered Quick、必要な Functional/Full、fresh static review、修正、scoped local commit を完了してから自動的に次へ進む。
4. 通常の API 差異、test failure、review finding、文書差異を理由にユーザー承認待ちへ移行しない。原因を直して同じ Unit を完了する。
5. current non-commercial classification と GREEN への復帰は accepted decision である。commercial license の不存在を理由に decision を再オープンしない。primary source が materially changed して accepted release fact と明確に矛盾する証拠が出た場合だけ、その証拠を exact source/date と共に blocker として扱う。
6. Unit ごとに explicit paths のみ stage し、以下の local commit を作る。commit 前後に `git diff --cached --check` と `git status --short` を確認する。
7. push、tag、release、version bump、force-push、rebase、filter-repo、history rewrite、native binary update、ManagedBass version update は行わない。
8. BASS.NET registration value を復号、再構成、表示、log 出力しない。
9. 最終応答は全 Completion Gate を評価してから一度だけ返す。中間 unit の完了を応答境界にしない。

## 5. 実行 Unit

## Unit 1 — Native BASS compliance records を current non-commercial GREEN へ整合

### Observable outcome

英日ノーティス、README、ADR、manual acceptance、license inventory が、current non-commercial release を一貫して GREEN とし、future commercial use を policy-change condition として分離する。六 DLL の著作権と license scope が正確になる。

### 主な変更対象

- `ThirdPartyNotices.txt`
- `ThirdPartyNotices.ja.txt`
- `third_party/licenses/01-BASS-NOTICE.txt`
- 新規 `third_party/licenses/01a-BASSASIO-NOTICE.txt`
- 新規 `third_party/licenses/01b-BASS_FX-NOTICE.txt`
- `README.md`
- `README.ja.md`
- `devdocs/decisions/managedbass-adoption.md`
- `devdocs/plan/BeMusicSeeker_refactoring_plans/POST_MIGRATION_MANUAL_ACCEPTANCE.md`
- `BeMusicSeeker.Tests/ManagedDependencyOutputPolicyTests.cs`

### 実装手順

1. retained binary の file/product version、copyright metadata、SHA-256 を再計測する。binary は変更しない。
2. exact retained BASS_FX 2.4.12.6 の upstream archive/readme を一次配布元から取得できる場合は、archive URL、取得日、archive hash、readme の license section hash を `01b-BASS_FX-NOTICE.txt` に記録する。取得不能でも、Un4seen の current BASS_FX 2.4.12.6 listing、retained DLL metadata、作者の official disclaimer を記録し、根拠を隠さない。
3. native entry を次の三つへ分割する。
   - BASS core + official add-ons: `bass.dll`, `bassmix.dll`, `bassenc.dll`, `basswasapi.dll`
   - BASSASIO: `bassasio.dll`
   - BASS_FX: `bass_fx.dll`
4. `bass_fx.dll` の copyright を `(: JOBnik! :) [Arthur Aminov, ISRAEL]` とし、Un4seen への誤帰属を除く。
5. `01-BASS-NOTICE.txt` は authoritative full license text と誤認させない。`Notice Summary` と明記し、BASS core と official add-ons の source/version/terms を記録する。BASSASIO と BASS_FX は別 notice file を参照する。
6. Action summary を次の状態へ変更する。
   - RED: none
   - YELLOW: none
   - GREEN: BASS core + official add-ons、BASSASIO、BASS_FX、ManagedBass、および既存 GREEN components
7. summary の直後または各 entry に「commercial/monetized release へ方針変更した場合は release 前に該当 commercial license を取得・再確認する」と記載する。これは current status を YELLOW にしない。
8. `managedbass-adoption.md` の external prerequisite を分割する。
   - Native license: current non-commercial release は confirmed / GREEN。
   - Future commercial release: policy-change review required。
   - Historical BASS.NET registration material: separate security gate。
9. `POST_MIGRATION_MANUAL_ACCEPTANCE.md` の `RELEASE-01` を current non-commercial distribution rights completed とし、historical registration material は別 ID (`SECURITY-01`) で pending external action とする。`MANUAL-02` は従来どおり non-blocking のまま維持する。
10. README は “may be allowed” の曖昧表現をやめ、current non-commercial release と future commercial condition を簡潔に記載する。

### Tests

`ManagedDependencyOutputPolicyTests` に少なくとも次を追加する。

- English/Japanese の各 native entry が `GREEN`。
- Action summary の `YELLOW` が `(None)` / `(なし)` である。
- GREEN summary に BASS core/add-ons、BASSASIO、BASS_FX、ManagedBass が含まれる。
- `bass_fx.dll` が Un4seen copyright entry に含まれず、JOBnik / Arthur Aminov entry に含まれる。
- `bassasio.dll` が BASS core/add-ons entry ではなく BASSASIO entry に含まれる。
- three notice files が存在し、英日ノーティスから参照される。
- notice summary を `License Text` と誤記していない。
- native six-file set、versions、hashes が従来どおりである。
- commercial monetization trigger の注記がある一方、current release を YELLOW にしていない。

### Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ManagedDependencyOutputPolicyTests'
```

prose/license files は UTF-8 without BOM、LF、trailing whitespace なし、参照先存在、`git diff --check` を確認する。

### Static review scope

- current non-commercial decision と upstream terms の整合
- copyright / license scope の分離
- EN/JA parity
- publish/package が三 notice file を含むこと
- future commercial condition と current status の混同が無いこと

### Commit

```text
docs(compliance): restore non-commercial BASS distribution to green
```

## Unit 2 — Real external encoder opt-in smoke coverage

### Observable outcome

少なくとも一つの実外部 encoder executable を使って、production ManagedBass/BASSenc writer route が deterministic PCM を有効な output へ変換できる。通常 Functional/Full は tool 不在で失敗しないが、明示 opt-in 実行は設定誤りを黙って pass しない。

### 主な変更対象

- 新規 `BeMusicSeeker.Tests/ExternalAudioEncoderSmokeTests.cs`
- 必要に応じて小さい test helper。production abstraction は smoke test のためだけに増やさない。
- `devdocs/spec/testing-strategy.md`
- `BeMusicSeeker.Tests/TestData/README.md` または encoder fixture の最小説明

### Test contract

1. `[TestCategory("ProcessIntegration")]` を付ける。
2. opt-in flag は `BMS_TEST_AUDIO_ENCODERS=1`。
3. encoder folder は `BMS_TEST_AUDIO_ENCODER_DIR`。未指定時は production search order を使ってよい。
4. optional `BMS_TEST_AUDIO_ENCODER_TYPES` で `MP3_LAME,AAC_NERO,OPUS,FLAC,OGG_VORBIS` の required subset を指定できる。
5. flag が無い通常 run は deterministic `Assert.Inconclusive` とする。
6. flag があり required subset が指定されている場合、required executable が無ければ fail する。
7. flag があり subset 未指定の場合、見つかった executable をすべて試し、一つも見つからなければ fail する。opt-in を全 skip で成功扱いしない。
8. test input は test 内で生成する短い deterministic stereo PCM/WAV とし、copyrighted media を追加しない。
9. production `BassAudioWriter` の NULL_DEVICE session、encoder creation、tag setup、start、pull rendering、stop、cleanup を通す。command factory だけを直接 process 起動する代替 test にしない。
10. output collision suffix、Unicode/space path を一ケース含める。
11. output validation は最低限次を確認する。
    - file exists and non-empty
    - MP3: ID3 または MPEG frame sync
    - Nero AAC/M4A: ISO BMFF `ftyp`
    - Opus: `OggS` と `OpusHead`
    - FLAC: `fLaC`
    - Ogg Vorbis: `OggS` と Vorbis identification marker
12. test 終了時に encoder process、audio session、temporary files を残さない。failure 時も cleanup を `finally` で実施し、primary failure を cleanup failure で上書きしない。
13. external encoder のライセンスや binary を repository / package に追加しない。

### Verification

通常 regression:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~AudioEncoderCommandFactoryTests|FullyQualifiedName~AudioEncoderSessionTests|FullyQualifiedName~BassAudioWriterTests|FullyQualifiedName~ExternalAudioEncoderSmokeTests'
```

実 tool opt-in:

```powershell
$env:BMS_TEST_AUDIO_ENCODERS = '1'
$env:BMS_TEST_AUDIO_ENCODER_DIR = '<folder containing available encoder executables>'
# Optional, when an exact required set is available:
$env:BMS_TEST_AUDIO_ENCODER_TYPES = 'MP3_LAME,OPUS,FLAC,OGG_VORBIS'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ExternalAudioEncoderSmokeTests'
```

少なくとも一つの real encoder で pass を記録する。Nero AAC が利用不能でも、command contract tests と利用可能な他 format の real smoke が pass すれば Unit を閉じてよい。利用可能な executable が本当に一つも無い環境では test 実装と deterministic opt-in failure contract を完成させ、実行コマンドを記録するが、最終 migration completion note には「real encoder run 未実施」と明示する。

### Static review scope

- production writer route を実際に通っていること
- external process lifecycle と timeout/cleanup
- opt-in disabled/enabled の pass/skip/fail semantics
- no bundled encoder binary
- Functional 180 秒予算への影響が無いこと

### Commit

```text
test(audio): add opt-in external encoder smoke coverage
```

## Unit 3 — Migration documentation closure and final acceptance

### Observable outcome

恒久仕様と判断が正本へ統合され、完了済み migration plan が active plan から退役し、current tree と documentation が「ManagedBass migration completed」と一貫している。

### 主な変更対象

- `devdocs/decisions/managedbass-adoption.md`
- `devdocs/spec/audio-runtime-phase1.md`
- `devdocs/spec/bass-runtime-dependency-set.md`
- `devdocs/spec/testing-strategy.md`
- `devdocs/plan/bassnet-to-managedbass-migration-plan.md`（退役対象）
- この closeout plan（完了後の退役対象）

### 実装手順

1. ADR に completion evidence を短く追加する。
   - exact ManagedBass package set
   - native six-DLL invariants
   - encoder replacement boundary
   - final verification commands/results
   - current non-commercial native status GREEN
   - future monetization trigger
   - historical registration material security gate
2. `audio-runtime-phase1.md` と `testing-strategy.md` に real encoder opt-in contract を正本として記載する。
3. 元 migration plan の historical verification row `Notice classification correction` は削除して歴史を書き換えず、その後に superseding correction を記録する。YELLOW 化は current policy を誤って hypothetical commercial case と混同したため撤回され、Unit 1 commit で GREEN へ復帰したことを明記する。
4. 元 migration plan の explicit real encoder smoke gap が Unit 2 で閉じたことを記録する。
5. 恒久情報を正本へ統合後、`devdocs/plan/bassnet-to-managedbass-migration-plan.md` を削除する。Git history が詳細な実行履歴を保持するため、同内容を別 active plan へ複製しない。
6. 本 closeout plan も Completion Gate 達成後に削除する。未完了なら active plan として残し、Status/remaining items を正確に更新する。
7. `POST_MIGRATION_MANUAL_ACCEPTANCE.md` は `MANUAL-02` が残るため active plan として維持してよい。

### Final verification

Unit 1/2 の filtered Quick が pass した後に次を実行する。

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

追加 static checks:

```powershell
git grep -n -I -E 'Un4seen\.Bass|Bass\.Net\.dll|BASS\.NET' -- ':!devdocs/decisions/**' ':!BeMusicSeeker.Updater/**' ':!BeMusicSeeker.Tests/**'
git diff --check
git status --short --branch
```

- updater legacy cleanup と tests の否定 assertion 以外に `Bass.Net.dll` current dependency が無いこと。
- `Un4seen.Bass` production reference が zero。
- exact ManagedBass six-package set が app/test output policy と一致。
- native six DLL hashes が spec と一致。
- English/Japanese notice summary が GREEN/YELLOW/RED で一致。
- Full publish/package に three native notice summaries と ManagedBass MIT text が含まれる。
- verification 後に tracked files が変化せず、残留 process が無い。

implementation と standard verification 後、`.codex/agents/repo-static-review.toml` の fresh `repo-static-review` を呼ぶ。P0/P1 と acceptance-direct P2 は修正し、affected Quick を再実行する。release/package contract に影響した修正では Full も再実行する。

### Commit

```text
docs(migration): close ManagedBass migration
```

## 6. Completion Gate

次をすべて満たしたときだけ closeout 完了とする。

- [ ] current source/package/output に BASS.NET managed wrapper、registration call、registration material が無い。
- [ ] ManagedBass six packages は exact 4.0.2 のまま。
- [ ] native six DLL の version/hash は変更されていない。
- [ ] BASS core + official add-ons、BASSASIO、BASS_FX、ManagedBass が英日ノーティスで GREEN。
- [ ] RED は none、YELLOW は none。
- [ ] future commercial/monetized release condition は注記されているが current status を YELLOW にしていない。
- [ ] `bass_fx.dll` の copyright/license が JOBnik / Arthur Aminov へ正しく帰属されている。
- [ ] BASSASIO が BASS core license と別 entry になっている。
- [ ] license inventory が summary と authoritative source の区別を誤表示していない。
- [ ] real external encoder opt-in smoke test が実装され、少なくとも一つの available encoder で pass、または tool 不在を明示した deterministic execution record がある。
- [ ] Functional が command 全体 180 秒以内で pass。
- [ ] Full lane が pass。
- [ ] fresh static review に blocking finding が無い。
- [ ] completed migration plan と completed closeout plan が active `devdocs/plan` から退役している。
- [ ] historical BASS.NET registration material は current native license status と分離された external security gate として記録され、値は表示・復号されていない。
- [ ] three scoped local commits が作成され、最終 worktree が clean。
- [ ] push/tag/release/version/history rewrite は行われていない。
