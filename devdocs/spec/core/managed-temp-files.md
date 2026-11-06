# 一時ファイルの管理

## 目的と適用範囲

URL取得とアーカイブ導入で作る一時ファイルを、利用者のファイルと区別して管理します。終了時の一括削除だけに依存せず、不要になったものから回収します。入力の受付と受渡しは[ドロップ導入](../library/drop-install.md)と[URL取得](../playlist/downloads.md)を正本とし、本書は削除してよい範囲と時機を定めます。

## 用語

[共通用語集](../glossary.md)の後片付けを使います。

| 用語 | 意味 |
| --- | --- |
| 管理一時領域 | `%TEMP%\BeMusicSeeker\` の下に、アプリがセッションごとに作るディレクトリ。 |
| セッション | 一つのプロセスが所有する管理一時領域の単位。名前は `session-` で始まり、アプリが作ったことを示す作成印を持つ。 |
| 管理一時パス | 現在のプロセスが `TempDirectoryPublisher` で作ったセッション配下のパス。名前が似ているだけの一時フォルダは含まない。 |

## 仕様

### 所有範囲

アプリが削除してよいのは、作成印を持つセッションの配下だけです。次のものは、同名・同内容であっても削除しません。

- 利用者がドロップまたは選択した通常のファイル。
- 導入済みの譜面。
- 現在のセッションの外に手動で置いたファイル。

管理一時パスかどうかの判定は、削除の権限を決めるものです。入力を受け付ける許可の一覧には使いません。

### 領域の取得

`TempDirectoryPublisher` は、取得のたびに、同じセッションの中の独立したディレクトリを返します。返却した時点で、セッションの作成印の書込みは完了しています。同じプロセスから初回の取得が並列に始まっても、作成印の初期化は競合しません。

初期化の実際のI/Oが失敗した場合は、取得元へ伝播します。再試行や別の領域への置換で成功扱いにしません。終了処理が始まった後は、新しい領域を取得できません。

### 削除の時機

| 時機 | 回収するもの・残すもの |
| --- | --- |
| 起動時 | `Application_Startup` でアプリのミューテックスを取得した後、過去のセッションの回収を非同期に開始する。起動はその完了を待たない。対象は、作成印を持ち、使用中でない（生存していない）過去のセッションだけ。現在のセッション、他のプロセスが使用中のセッション、作成印のない領域は削除しない。 |
| URL取得後 | 未受理の場合は、要求が所有する一時領域を一回回収し、取得のTaskが実際の終端を待つ。受理後は待ち行列へ、導入の開始直前からは導入側へ寿命を移す。開始前の取消や世代の退役では、未消費の入力の回収と実際の停止を待つ。開始後は、取得側で無条件には回収しない。 |
| 展開の正常終了 | 管理元のアーカイブを消費し、展開先を自動導入・保留登録・直接推定へ渡す。利用者が所有する元のアーカイブは保持する。 |
| 展開の失敗、準備中の例外、適用前の対象なし、取消 | 準備の開始から、操作が管理元の入力と作成済みの展開先を把握する。引き渡していない管理入力は、未展開の後続の入力も含めて、操作の終端までに回収する。 |
| 保留中 | 操作に必要な展開先と、取得した単体の譜面を残す。取得のTaskと導入の管理主体が実際に停止した後も、保留の理由によらずバイト列を保持し、後の明示的な手動導入に使う。 |
| 適用開始後の失敗・取消 | 確定済みの保留・導入と、部分的な物理変更の後の入力と確認候補を、[ファイルとDBの整合性](../library/file-db-consistency.md)に従って保全する。外側の終端処理で無条件には削除しない。 |
| 保留一覧から削除 | パッケージのパスが管理一時パスの場合だけ削除する。利用者所有のパスは消さない。 |
| アプリ終了 | 新しい取得を止め、開始済みの過去のセッションの回収が終わってから、現在のセッションを削除する。削除に失敗しても終了を続ける。 |

起動時の回収は、使用中のセッションと過去のセッションを、作成印の排他的な保持で区別します。作成印を開けない、内容が現行の形式と一致しないなど、所有を確認できないセッションは残します。同じ管理一時領域を使う複数のプロセスの回収は、互いに重ならないように順に行います。

### 失敗の扱い

過去のセッションの回収、保留一覧からの削除、終了時の削除は、容量を回収するための補助処理です。失敗はログに残しますが、取得・導入・保留一覧からの削除の結果は変えません。強制終了などで回収できなかった管理領域は、次回起動時の過去のセッションの回収で扱います。

URL取得の要求が所有する一時領域の回収は、要求の終端に含めます。この回収の例外は、その要求の失敗として残します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 作成印、セッション内外、管理下にないファイルの保護 | [`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs)、[`TemporaryDirectorySession`](../../../BeMusicSeeker/Runtime/TemporaryDirectorySession.cs) | [`TempDirectoryPublisherTests`](../../../tests/BeMusicSeeker.Core.Tests/FileOperations/TempDirectoryPublisherTests.cs) |
| 並列の初回取得、返却時の作成印、同じセッションの中の独立した領域 | `TempDirectoryPublisher.Get`、`TemporaryDirectorySession.Get` | `TempDirectoryPublisherTests.ConcurrentFirstGetsReturnDistinctDirectoriesWithCompletedSessionMarker` が、製品を分離してロードし、未使用のセッションから並列に取得して、各返却の直後に作成印を読む。内部のI/Oの交錯は強制しない。存在確認から書込み完了までの排他と失敗の伝播は、静的に確認する。 |
| 起動時の過去のセッションの回収と、使用中のセッションの保持 | `TemporaryDirectorySession.StartCleanupStaleDirectoriesAsync`、[`App`](../../../BeMusicSeeker/App.cs) | `TempDirectoryPublisherTests.CleanupStaleDirectoriesDeletesOnlyKnownInactiveSessions` が、使用中のセッション、手動のファイル、作成印のない領域、形式の異なる作成印を保持し、現行の作成印を持つ未使用のセッションだけを削除することを確認する。起動時の呼出し位置は静的に確認する。 |
| 並列に確保した入力の寿命と、一括の解放 | [`temporarilyCopyFiles`](../../../BeMusicSeeker/ViewModels/ChartOperations/temporarilyCopyFiles.cs) | [`TemporaryCopyFilesTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/TemporaryCopyFilesTests.cs) |
| 展開・保留・削除に伴う寿命 | [ドロップ導入](../library/drop-install.md)と[ライブラリ変更](../library/mutations.md) | 各仕様の実入口のテストで確認する。 |
| URL取得の入力の所有の移転と、保留の保持 | [URL取得](../playlist/downloads.md) | `PlaylistUrlAcquisitionOwnershipTests.DownloadedChart_RealEstimationRetainsPendingBytesForLaterManualInstall` が、実際の推定後の保留の正本・DB・バイト列と、後の明示的な導入を確認する。開始前の回収、未受理、実際の導入中の寿命は、同じクラスの所有のテストが分担する。 |
| 形式別の保留と、入れ子の導入の局所的な回収 | 同上 | [`BmsLibraryPackageInstallServiceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryManagedArchiveInstallTests.cs) が、実際の7z・RAR・LZHの保留とZIPの入れ子の導入で、借用元を保ち、検証データ自身の展開先のルートだけを回収することを確認する。共有するセッションは削除せず、管理一時領域を共有する系列との並列実行を避ける。 |
| 正常な管理元の消費と、未引渡しの入力の回収 | [`BMSLibrary.PackageInstall`](../../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs)、[`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Install/BmsLibraryManagedArchiveInstallTests.cs) の `AutoInstall_ArchiveAndFolderInputsReachInstallOrPendingEstimation` が、管理ZIP・借用ZIP・フォルダの導入・保留と推定を確認する。[`PackageInstallWorkflowOwnerTests`](../../../tests/BeMusicSeeker.Windows.Tests/Install/PackageInstallWorkflowOwnerArchiveTests.cs) の `ProductionImport_PreApplyTerminalReclaimsManagedInputsBeforeAdmissionRelease` が、展開直後の取消、停止の失敗、譜面なし、実際の展開先の消失での回収と、利用者の元のファイルの保護を確認する。 |
| 終了時の回収 | `TemporaryDirectorySession.RemoveAllAsync`、[終了処理](../runtime/shutdown.md) | `TempDirectoryPublisherTests.RemoveAllStopsGetsAndDrainsCleanupBeforeReleasingLease` が、新しい取得の停止、開始済みの回収の終端までの現在のセッションの保持、その後の削除を確認する。終了処理との接続は終了仕様の対応表で確認する。 |

## 関連資料

[ドロップ導入](../library/drop-install.md)、[URL取得](../playlist/downloads.md)、[終了](../runtime/shutdown.md)。
