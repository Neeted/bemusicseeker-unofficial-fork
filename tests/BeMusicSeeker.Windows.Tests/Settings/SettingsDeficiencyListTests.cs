using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.Tests.SettingsDialogHarness;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>設定の不備の一覧（生成・並び・作り直しの時機）と、前提を要する有効化チェックの欄の理由を実VM・実設定XML・実FSで検証します。</summary>
/// <remarks>Windowを使わないVM境界です。案内欄・ページの印・行の操作の表示は <see cref="SettingsWindowDeficiencyGuidanceTests"/> が分担します。共有 culture を変更するケースがあるため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingsDeficiencyListTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>
    /// 有効な基準から確定値を1か所だけ崩し、一覧が契約の表どおりのページ・対象欄・現行の検査と同じ理由になることと、
    /// 一覧が空であることと開始ボタンの可否が一致することを確認します（K1・K4）。
    /// 「valid」は空で開始可能、他の行はすべて空でなく開始不可です。
    /// </summary>
    /// <remarks>
    /// 誤ったページや欄への割当て、出力先の検査例外を原因以外の欄へ付ける誤り、重複・重なりを先に検査した側へ付ける誤り、
    /// スタンドアロンでLR2バックアップの不備を出す誤り、設定XMLの解決失敗を一覧から落とす誤り、
    /// LR2環境の行がある間の出力先・導入先の行の抑止漏れと抑止し過ぎ、未設定の出力先の重複行、
    /// BMSディレクトリが一件もない場合の行の割当てと導入先の行の重複、導入先の理由の選択誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow("valid")]
    [DataRow("lr2-songdb")]
    [DataRow("lr2-config")]
    [DataRow("lr2-root-unset-songdb-missing")]
    [DataRow("lr2-prerequisite-order")]
    [DataRow("lr2-no-registered-root")]
    [DataRow("nested-roots")]
    [DataRow("standalone-no-root")]
    [DataRow("standalone-install-same-as-leftover-output")]
    [DataRow("score-db")]
    [DataRow("beatoraja-root")]
    [DataRow("bmt-root")]
    [DataRow("image")]
    [DataRow("ubmplay")]
    [DataRow("lr2body")]
    [DataRow("normal-unset")]
    [DataRow("root-unset")]
    [DataRow("root-sjis")]
    [DataRow("normal-unset-root-sjis")]
    [DataRow("additional-duplicate-name")]
    [DataRow("additional-overlap")]
    [DataRow("install")]
    [DataRow("install-empty")]
    [DataRow("install-same-as-normal-output")]
    [DataRow("install-under-root-output")]
    [DataRow("install-under-additional-output")]
    [DataRow("lr2-backup")]
    [DataRow("standalone-backup")]
    public void DeficiencyList_AssignsEachConfirmedFailureToContractPageFieldAndReason(string breakCase)
    {
        RunOnUiDispatcher(() =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            bool standalone = breakCase is "standalone-no-root" or "standalone-backup" or "standalone-install-same-as-leftover-output";
            Settings values = standalone ? CreateStandalone(directory.Path) : CreateLr2(directory.Path);
            string lr2Root = Path.Combine(directory.Path, "lr2");
            string missing = Path.Combine(directory.Path, "missing");
            var expected = new List<(SettingsCategory Category, SettingsDeficiencyField Field, string? Reason)>();
            switch (breakCase)
            {
                case "valid": break;
                case "lr2-songdb":
                    values.LR2SongDBPath = Path.Combine(missing, "song.db");
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_InvalidLR2SongDbOrConfigPath));
                    break;
                case "lr2-config":
                    File.WriteAllText(values.LR2ConfigXmlPath, "<config><broken>");
                    // LR2環境の行がある間は、検索ルートを読めないことに由来する導入先の行を出しません。
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_InvalidLR2SongDbOrConfigPath));
                    break;
                case "lr2-root-unset-songdb-missing":
                    // 保存値が空なら、song.dbが使えない理由もLR2ディレクトリの設定を促します。
                    values.LR2RootPath = string.Empty;
                    values.LR2SongDBPath = Path.Combine(missing, "song.db");
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_Lr2RootPathNotSet));
                    break;
                case "lr2-prerequisite-order":
                    // 前提のLR2環境の行がある間は、出力先・導入先の行を出さず、前提と無関係なページの行は出します。
                    values.LR2SongDBPath = Path.Combine(missing, "song.db");
                    values.LR2CustomFolderOutputBaseDir = string.Empty;
                    values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
                    values.BMSInstallDir = missing;
                    values.UseExternalPanelImage = true;
                    values.StagefilePath = Path.Combine(missing, "stagefile.png");
                    values.IsLR2BackupEnabled = true;
                    values.LR2BackupPath = missing;
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_InvalidLR2SongDbOrConfigPath));
                    expected.Add((SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath));
                    expected.Add((SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput, Resources.Error_LR2BackupPathNotSet));
                    break;
                case "lr2-no-registered-root":
                    // 登録済みのBMSディレクトリがなければ、一般ページのBMSディレクトリ欄の一行にまとめ、導入先の行は出しません。
                    RemoveAllSearchRoots(values.LR2ConfigXmlPath);
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, Resources.Error_BmsDirectoryAndInstallRequired));
                    break;
                case "nested-roots":
                    {
                        AddNestedSearchRoot(values.LR2ConfigXmlPath, Path.Combine(lr2Root, "Songs", "Child"));
                        expected.Add((SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, null));
                        break;
                    }
                case "standalone-no-root":
                    values.StandaloneBmsRootPaths = string.Empty;
                    values.BMSRootPath = string.Empty;
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, Resources.Error_BmsDirectoryAndInstallRequired));
                    break;
                case "standalone-install-same-as-leftover-output":
                    // スタンドアロンでは出力先を使わないため、残っている出力先の値と同じでも一般の理由です。
                    values.LR2CustomFolderOutputBaseDir = missing;
                    values.BMSInstallDir = missing;
                    expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDirNotRegistered));
                    break;
                case "score-db":
                    {
                        string beatoraja = CreateBeatorajaRoot(directory.Path, "beatoraja");
                        values.BeatorajaRootPath = beatoraja;
                        values.BeatorajaPlayerId = "player";
                        values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "player", "score.db");
                        File.Delete(values.BeatorajaScoreDbPath);
                        values.UseBeatorajaScoreDb = true;
                        expected.Add((SettingsCategory.General, SettingsDeficiencyField.BeatorajaScoreDb, Resources.Error_InvalidBeatorajaScoreDbPath));
                        break;
                    }
                case "beatoraja-root":
                    values.BeatorajaRootPath = missing;
                    values.UseBeatorajaScoreDb = true;
                    // スコアDBはルートから解決するため、ルートが無効な間はルートの項目だけを出します。
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.BeatorajaRoot, Resources.Error_InvalidBeatorajaRootPath));
                    break;
                case "bmt-root":
                    values.BeatorajaRootPath = missing;
                    values.EnableBeatorajaBmtOutput = true;
                    expected.Add((SettingsCategory.General, SettingsDeficiencyField.BeatorajaRoot, Resources.Error_InvalidBeatorajaRootPath));
                    break;
                case "image":
                    values.UseExternalPanelImage = true;
                    values.StagefilePath = Path.Combine(missing, "stagefile.png");
                    expected.Add((SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath));
                    break;
                case "ubmplay":
                    values.UsePlayeruBMplay = true;
                    values.uBMplayPath = Path.Combine(missing, "uBMplay.exe");
                    expected.Add((SettingsCategory.Playback, SettingsDeficiencyField.PlayeruBMplay, Resources.Error_InvalidUBMPlayExecutablePath));
                    break;
                case "lr2body":
                    {
                        values.UsePlayerLR2body = true;
                        string executable = StartupSettingsSnapshot.CreateCurrent(values).LR2bodyPath;
                        File.Delete(executable);
                        // 実行ファイルがないLR2ディレクトリは無効でもあるため、現行の検査順で二つの理由を同じ欄へ並べます。
                        expected.Add((SettingsCategory.Playback, SettingsDeficiencyField.PlayerLR2body, Resources.Error_InvalidLR2RootPath));
                        expected.Add((SettingsCategory.Playback, SettingsDeficiencyField.PlayerLR2body,
                            string.Format(CultureInfo.CurrentCulture, Resources.Error_LR2ExecutableNotFoundFormat, executable)));
                        break;
                    }
                case "normal-unset":
                    values.LR2CustomFolderOutputBaseDir = string.Empty;
                    // 未設定の出力先は「未設定」の一行だけで示し、「選択されていません」の行を重ねません。
                    expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.NormalOutput, Resources.Error_CustomFolderOutputPathNotSet));
                    break;
                case "root-unset":
                    values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
                    expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput, Resources.Error_CustomFolderRootOutputPathNotSet));
                    break;
                case "root-sjis":
                    // Shift_JIS で表せない文字の検査例外は、原因のROOT出力先の欄へ割り当てます。
                    values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(lr2Root, "Rooté")).FullName;
                    expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput, null));
                    break;
                case "normal-unset-root-sjis":
                    // 未設定の出力先を除いた残りに従来の検査を行い、文字種の違反は原因のROOT出力先の欄へ割り当てます。
                    values.LR2CustomFolderOutputBaseDir = string.Empty;
                    values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(lr2Root, "Rooté")).FullName;
                    expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.NormalOutput, Resources.Error_CustomFolderOutputPathNotSet));
                    expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput, null));
                    break;
                case "additional-duplicate-name":
                    {
                        string other = Directory.CreateDirectory(Path.Combine(directory.Path, "other", "NormalOutput")).FullName;
                        values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([other]);
                        // 名前の重複は後に検査した追加出力先の欄です。
                        expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.AdditionalOutput,
                            string.Format(CultureInfo.CurrentCulture, Resources.Validation_OutputBaseNameDuplicateFormat, "NormalOutput")));
                        break;
                    }
                case "additional-overlap":
                    {
                        string nested = Directory.CreateDirectory(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Nested")).FullName;
                        values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([nested]);
                        // 重なりは後に検査した追加出力先の欄です。
                        expected.Add((SettingsCategory.Playlist, SettingsDeficiencyField.AdditionalOutput, null));
                        break;
                    }
                case "install":
                    values.BMSInstallDir = missing;
                    expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDirNotRegistered));
                    break;
                case "install-empty":
                    values.BMSInstallDir = string.Empty;
                    expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InstallDirNotSelected));
                    break;
                case "install-same-as-normal-output":
                    values.BMSInstallDir = values.LR2CustomFolderOutputBaseDir;
                    expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDir));
                    break;
                case "install-under-root-output":
                    values.BMSInstallDir = Directory.CreateDirectory(Path.Combine(values.LR2CustomFolderOutputBaseDirRootType, "child")).FullName;
                    expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDir));
                    break;
                case "install-under-additional-output":
                    {
                        string additional = Directory.CreateDirectory(Path.Combine(directory.Path, "other", "Additional")).FullName;
                        values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
                        values.BMSInstallDir = Directory.CreateDirectory(Path.Combine(additional, "child")).FullName;
                        expected.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDir));
                        break;
                    }
                case "lr2-backup":
                    values.IsLR2BackupEnabled = true;
                    values.LR2BackupPath = missing;
                    expected.Add((SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput, Resources.Error_LR2BackupPathNotSet));
                    break;
                case "standalone-backup":
                    // スタンドアロンではLR2バックアップを使わないため、組が未成立でも項目を出しません。
                    values.IsLR2BackupEnabled = true;
                    values.LR2BackupPath = missing;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(breakCase));
            }
            values.Save();
            using var harness = SettingsDialogHarness.Create(values);
            IReadOnlyList<SettingsDeficiency> list = harness.Dialog.Deficiencies;
            string diagnostic = SettingsDeficiency.FormatDiagnostic(list);
            CollectionAssert.AreEqual(expected.Select(item => (item.Category, item.Field)).ToArray(),
                list.Select(item => (item.Category, item.Field)).ToArray(), diagnostic);
            for (int index = 0; index < expected.Count; index++)
            {
                if (expected[index].Reason is string reason) { Assert.AreEqual(reason, list[index].Reason, diagnostic); }
                else { Assert.IsFalse(string.IsNullOrWhiteSpace(list[index].Reason), diagnostic); }
                Assert.AreEqual(SettingsDeficiency.GetCategoryName(list[index].Category), list[index].CategoryName);
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat,
                    list[index].CategoryName, list[index].Reason), list[index].DisplayText);
            }
            // 公開中の一覧は確定値から作った一覧と同じ内容で、開始ボタンの可否は一覧の空と一致します。
            CollectionAssert.AreEqual(list.Select(item => item.DisplayText).ToArray(),
                harness.Dialog.BuildDeficiencies().Select(item => item.DisplayText).ToArray());
            Assert.AreEqual(list.Count > 0, harness.Dialog.HasDeficiencies);
            Assert.AreEqual(list.Count == 0, harness.Dialog.CanStartEnvironment, diagnostic);
            return Task.CompletedTask;
        }, nameof(DeficiencyList_AssignsEachConfirmedFailureToContractPageFieldAndReason));
    }

    /// <summary>
    /// 複数ページ・同じページ内に複数の不備がある確定値で、一覧がページ一覧の順、同じページ内は現行の検査順
    /// （一般: LR2環境 → BMSディレクトリ → スコアDB → beatorajaルート → BMT出力）に並ぶことを確認します（K2）。
    /// </summary>
    /// <remarks>検査順やページの並びを崩す並び替え、同じページ内の順序を保たない並び替えを検出します。スコアDBとルートは同時には出ないため、二つの一覧で検査順を確かめます。</remarks>
    [TestMethod]
    public void DeficiencyList_OrdersByPageListThenCurrentCheckOrder()
    {
        RunOnUiDispatcher(() =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = CreateLr2(directory.Path);
            string lr2Root = Path.Combine(directory.Path, "lr2");
            string missing = Path.Combine(directory.Path, "missing");
            // ページ一覧の後ろのページから順に崩し、検査の記述順と並びが偶然一致しないようにします。
            values.IsLR2BackupEnabled = true;
            values.LR2BackupPath = missing;
            values.BMSInstallDir = missing;
            values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            values.UsePlayeruBMplay = true;
            values.uBMplayPath = Path.Combine(missing, "uBMplay.exe");
            values.UseExternalPanelImage = true;
            values.StagefilePath = Path.Combine(missing, "stagefile.png");
            string beatoraja = CreateBeatorajaRoot(directory.Path, "beatoraja");
            values.BeatorajaRootPath = beatoraja;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "player", "score.db");
            File.Delete(values.BeatorajaScoreDbPath);
            values.UseBeatorajaScoreDb = true;
            AddNestedSearchRoot(values.LR2ConfigXmlPath, Path.Combine(lr2Root, "Songs", "Child"));
            values.Save();
            // LR2環境が成立する間は、プレイリスト（出力先）とインストール（導入先）の行がページ順どおりに並びます。
            using (var harness = SettingsDialogHarness.Create(values))
            {
                CollectionAssert.AreEqual(new[]
                {
                    (SettingsCategory.General, SettingsDeficiencyField.BmsDirectories),
                    (SettingsCategory.General, SettingsDeficiencyField.BeatorajaScoreDb),
                    (SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage),
                    (SettingsCategory.Playback, SettingsDeficiencyField.PlayeruBMplay),
                    (SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput),
                    (SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory),
                    (SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput)
                }, harness.Dialog.Deficiencies.Select(item => (item.Category, item.Field)).ToArray(),
                    SettingsDeficiency.FormatDiagnostic(harness.Dialog.Deficiencies));
            }
            // LR2環境が不備なら先頭に加わり、一般ページ内はLR2環境 → BMSディレクトリ → beatorajaルートの順です（スコアDBに代わりルート）。
            values.LR2SongDBPath = Path.Combine(missing, "song.db");
            values.BeatorajaRootPath = missing;
            values.EnableBeatorajaBmtOutput = true;
            values.Save();
            using (var harness = SettingsDialogHarness.Create(values))
            {
                CollectionAssert.AreEqual(new[]
                {
                    SettingsDeficiencyField.Lr2Environment, SettingsDeficiencyField.BmsDirectories, SettingsDeficiencyField.BeatorajaRoot
                }, harness.Dialog.Deficiencies.Where(item => item.Category == SettingsCategory.General).Select(item => item.Field).ToArray(),
                    SettingsDeficiency.FormatDiagnostic(harness.Dialog.Deficiencies));
            }
            return Task.CompletedTask;
        }, nameof(DeficiencyList_OrdersByPageListThenCurrentCheckOrder));
    }

    /// <summary>
    /// 局所候補と画面外の確定値の変更では一覧を作り直さず、この画面からの項目保存・確定操作の終端・表示開始・表示言語の変更で作り直し、
    /// 一覧と開始ボタンの可否の変更通知を出すことを確認します（K3）。
    /// </summary>
    /// <remarks>局所候補を一覧へ混ぜる誤り、時機以外での再計算、時機での作り直し漏れ、言語変更後に旧言語の行を残す誤り、通知漏れを検出します。</remarks>
    [TestMethod]
    public void DeficiencyList_IgnoresLocalCandidatesAndRebuildsAfterConfirmationItemSaveReopenAndLanguageChange()
    {
        RunOnUiDispatcher(async () =>
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = CreateLr2(directory.Path);
            values.Save();
            string missing = Path.Combine(directory.Path, "missing");
            var dialogs = new RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            using var harness = SettingsDialogHarness.Create(values, dialogs: dialogs);
            SettingsDialogViewModel dialog = harness.Dialog;
            var changed = new List<string>();
            PropertyChangedEventHandler observe = (_, e) => changed.Add(e.PropertyName ?? string.Empty);
            dialog.PropertyChanged += observe;
            try
            {
                dialog.SetPresentationActive(true);
                IReadOnlyList<SettingsDeficiency> initial = dialog.Deficiencies;
                Assert.AreEqual(0, initial.Count);

                // 未成立の局所候補（保存されない画像の使用）は一覧を変えません。
                changed.Clear();
                dialog.UseExternalPanelImage = true;
                Assert.IsFalse(values.UseExternalPanelImage);
                Assert.AreSame(initial, dialog.Deficiencies);
                CollectionAssert.DoesNotContain(changed, nameof(SettingsDialogViewModel.Deficiencies));
                dialog.UseExternalPanelImage = false;

                // 画面外の確定値の変更は次の時機まで反映しません。
                values.Commit((nameof(Settings.UsePlayeruBMplay), true), (nameof(Settings.uBMplayPath), Path.Combine(missing, "uBMplay.exe")));
                Assert.AreSame(initial, dialog.Deficiencies);

                // この画面からの項目保存の終端で作り直し、一覧と開始ボタンの可否を通知します。
                changed.Clear();
                dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                CollectionAssert.AreEqual(new[] { SettingsDeficiencyField.PlayeruBMplay }, dialog.Deficiencies.Select(item => item.Field).ToArray());
                CollectionAssert.Contains(changed, nameof(SettingsDialogViewModel.Deficiencies));
                CollectionAssert.Contains(changed, nameof(SettingsDialogViewModel.HasDeficiencies));
                CollectionAssert.Contains(changed, nameof(SettingsDialogViewModel.CanStartEnvironment));

                // 作り直しても内容が同じなら一覧を差し替えず、案内欄の行を作り直させません。
                IReadOnlyList<SettingsDeficiency> afterSave = dialog.Deficiencies;
                changed.Clear();
                dialog.ShowRecommUpdatedMsg = !dialog.ShowRecommUpdatedMsg;
                Assert.AreSame(afterSave, dialog.Deficiencies);
                CollectionAssert.DoesNotContain(changed, nameof(SettingsDialogViewModel.Deficiencies));
                CollectionAssert.Contains(changed, nameof(SettingsDialogViewModel.CanStartEnvironment));

                // 表示開始で作り直します。
                values.Commit((nameof(Settings.UsePlayeruBMplay), false));
                Assert.AreEqual(1, dialog.Deficiencies.Count);
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                Assert.AreEqual(0, dialog.Deficiencies.Count);

                // 確定操作の終端で作り直します。
                values.Commit((nameof(Settings.UseExternalPanelImage), true), (nameof(Settings.StagefilePath), Path.Combine(missing, "stagefile.png")));
                Assert.AreEqual(0, dialog.Deficiencies.Count);
                changed.Clear();
                await dialog.CommitBmtSettingsAsync();
                CollectionAssert.AreEqual(new[] { SettingsDeficiencyField.ExternalImage }, dialog.Deficiencies.Select(item => item.Field).ToArray());
                CollectionAssert.Contains(changed, nameof(SettingsDialogViewModel.CanStartEnvironment));
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, "外観", Resources.Error_InvalidStagefilePath),
                    dialog.Deficiencies.Single().DisplayText);

                // 表示言語の変更で作り直し、行を新しい言語で作ります。
                values.Commit((nameof(Settings.UsePlayerBMIIDXView), true), (nameof(Settings.BMIIDXViewPath), Path.Combine(missing, "BMIIDXView.exe")));
                dialog.Language = "English";
                Assert.AreEqual("en-US", Resources.Culture?.Name);
                CollectionAssert.AreEqual(new[] { SettingsDeficiencyField.ExternalImage, SettingsDeficiencyField.PlayerBMIIDXView },
                    dialog.Deficiencies.Select(item => item.Field).ToArray());
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, Resources.Appearance, Resources.Error_InvalidStagefilePath),
                    dialog.Deficiencies[0].DisplayText);
                Assert.AreNotEqual("外観", dialog.Deficiencies[0].CategoryName);
                Assert.AreEqual(0, dialogs.MessageCount);
            }
            finally
            {
                dialog.PropertyChanged -= observe;
                dialog.SetPresentationActive(false);
            }
        }, nameof(DeficiencyList_IgnoresLocalCandidatesAndRebuildsAfterConfirmationItemSaveReopenAndLanguageChange));
    }

    /// <summary>
    /// スコア・外部画像・BMT・外部プレイヤーの有効化候補が前提を満たさない間、欄の直下に理由を出して保存せず、
    /// 成立（保存）・チェックを外す・取消のいずれかで理由が消えることを確認します（K8）。
    /// </summary>
    /// <remarks>
    /// 理由の取り違え（スコアはルート無効ならルート、BMTはルート無効ならルート）、未成立のまま保存する誤り、BMTの確定可否と確定時の検査の不一致、スコアの確定時の理由と欄の理由の不一致、
    /// 解消後の理由の残留を検出します。BMTの表の出力先なしは、有効なルートでは表の出力先が既定値へ解決されるため実FSで作れず、確定可否と確定時の検査が同じ条件を使うことはルート無効の行で確かめます。
    /// </remarks>
    [TestMethod]
    [DataRow("score-root")]
    [DataRow("score-db")]
    [DataRow("image")]
    [DataRow("bmt-root")]
    [DataRow("ubmplay")]
    [DataRow("bmiidxview")]
    public void UnmetPrerequisiteCandidates_ShowFieldReasonWithoutSavingAndClearWhenResolved(string kind)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = CreateLr2(directory.Path);
            string missing = Path.Combine(directory.Path, "missing");
            string beatoraja = CreateBeatorajaRoot(directory.Path, "beatoraja");
            values.BeatorajaRootPath = kind is "score-root" or "bmt-root" ? missing : beatoraja;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "player", "score.db");
            if (kind == "score-db") { File.Delete(values.BeatorajaScoreDbPath); }
            values.Save();
            var dialogs = new RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            SettingsDialogViewModel dialog = harness.Dialog;
            (SettingsDeficiencyField field, string reason) = kind switch
            {
                "score-root" => (SettingsDeficiencyField.BeatorajaScoreDb, Resources.Error_InvalidBeatorajaRootPath),
                "score-db" => (SettingsDeficiencyField.BeatorajaScoreDb, Resources.Error_InvalidBeatorajaScoreDbPath),
                "image" => (SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath),
                "bmt-root" => (SettingsDeficiencyField.BeatorajaBmtOutput, Resources.Error_InvalidBeatorajaRootPath),
                "ubmplay" => (SettingsDeficiencyField.PlayeruBMplay, Resources.Error_InvalidUBMPlayExecutablePath),
                "bmiidxview" => (SettingsDeficiencyField.PlayerBMIIDXView, Resources.Error_InvalidBMIIDXViewPath),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            string key = field.ToString();
            Assert.AreEqual(string.Empty, dialog.FieldReasons[key]);
            switch (kind)
            {
                case "score-root": case "score-db": dialog.UseBeatorajaScoreDb = true; break;
                case "image": dialog.UseExternalPanelImage = true; break;
                case "bmt-root": dialog.EnableBeatorajaBmtOutput = true; break;
                case "ubmplay": dialog.UsePlayeruBMplay = true; break;
                case "bmiidxview": dialog.UsePlayerBMIIDXView = true; break;
            }
            Assert.AreEqual(reason, dialog.FieldReasons[key]);
            Assert.AreEqual("Error", dialog.FieldReasonStatuses[key]);
            // 候補は保存せず、確定値だけから作る一覧も変えません。
            Assert.AreEqual(0, dialog.Deficiencies.Count);
            Settings saved = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
            Assert.IsFalse(saved.UseBeatorajaScoreDb || saved.UseExternalPanelImage || saved.EnableBeatorajaBmtOutput || saved.UsePlayeruBMplay || saved.UsePlayerBMIIDXView);
            switch (kind)
            {
                case "score-root":
                    await dialog.CommitScoreSettingsAsync();
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(reason, dialogs.LastMessageText, "確定時の検査は欄の理由と同じ理由を使います。");
                    Assert.IsFalse(values.UseBeatorajaScoreDb);
                    dialog.UseBeatorajaScoreDb = false;
                    break;
                case "score-db":
                    File.WriteAllBytes(values.BeatorajaScoreDbPath, []);
                    await dialog.CommitScoreSettingsAsync();
                    Assert.IsTrue(values.UseBeatorajaScoreDb, "前提を満たした候補は保存します。");
                    break;
                case "image":
                    {
                        string image = Path.Combine(directory.Path, "stagefile.png");
                        File.WriteAllBytes(image, []);
                        dialog.StagefilePath = image;
                        Assert.IsTrue(values.UseExternalPanelImage);
                        break;
                    }
                case "bmt-root":
                    Assert.IsFalse(dialog.CanConfirmBmtSettings);
                    await dialog.CommitBmtSettingsAsync();
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(reason, dialogs.LastMessageText, "確定時の検査は確定ボタンの可否と同じ理由を使います。");
                    Assert.IsFalse(values.EnableBeatorajaBmtOutput);
                    dialog.CancelBmtSettings();
                    break;
                case "ubmplay":
                    {
                        string player = Path.Combine(directory.Path, "uBMplay.exe");
                        File.WriteAllBytes(player, []);
                        dialog.uBMplayPath = player;
                        Assert.IsTrue(values.UsePlayeruBMplay);
                        break;
                    }
                case "bmiidxview":
                    dialog.UsePlayerBMIIDXView = false;
                    break;
            }
            Assert.AreEqual(string.Empty, dialog.FieldReasons[key]);
            Assert.AreEqual(0, dialog.Deficiencies.Count);
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }, nameof(UnmetPrerequisiteCandidates_ShowFieldReasonWithoutSavingAndClearWhenResolved));
    }

    /// <summary>
    /// 旧版で保存できた「有効化済み・前提なし」の組が一覧と欄の理由に出て、チェックを外す保存と前提を満たす修正のどちらでも消えることを確認します（K9）。
    /// </summary>
    /// <remarks>保存済みの不整合を見落とす誤り、解消の保存や表示開始時の再検査の後に項目・理由を残す誤りを検出します。</remarks>
    [TestMethod]
    [DataRow("bmt", "uncheck")]
    [DataRow("score", "uncheck")]
    [DataRow("score", "repair-reopen")]
    [DataRow("image", "uncheck")]
    [DataRow("image", "select")]
    public void SavedUnmetPairs_AreListedWithFieldReasonAndClearAfterEitherFix(string kind, string fix)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = CreateLr2(directory.Path);
            string missing = Path.Combine(directory.Path, "missing");
            string beatoraja = CreateBeatorajaRoot(directory.Path, "beatoraja");
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(beatoraja, "player", "player", "score.db");
            (SettingsDeficiencyField listed, SettingsDeficiencyField reasonField, string reason) = kind switch
            {
                "bmt" => (SettingsDeficiencyField.BeatorajaRoot, SettingsDeficiencyField.BeatorajaBmtOutput, Resources.Error_InvalidBeatorajaRootPath),
                "score" => (SettingsDeficiencyField.BeatorajaScoreDb, SettingsDeficiencyField.BeatorajaScoreDb, Resources.Error_InvalidBeatorajaScoreDbPath),
                "image" => (SettingsDeficiencyField.ExternalImage, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            switch (kind)
            {
                case "bmt": values.BeatorajaRootPath = missing; values.EnableBeatorajaBmtOutput = true; break;
                case "score": values.BeatorajaRootPath = beatoraja; File.Delete(values.BeatorajaScoreDbPath); values.UseBeatorajaScoreDb = true; break;
                case "image": values.UseExternalPanelImage = true; values.StagefilePath = Path.Combine(missing, "stagefile.png"); break;
            }
            values.Save();
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add);
            SettingsDialogViewModel dialog = harness.Dialog;
            dialog.SetPresentationActive(true);
            try
            {
                CollectionAssert.AreEqual(new[] { listed }, dialog.Deficiencies.Select(item => item.Field).ToArray());
                StringAssert.Contains(dialog.FieldReasons[reasonField.ToString()], reason);
                Assert.AreEqual("Error", dialog.FieldReasonStatuses[reasonField.ToString()]);
                Assert.IsFalse(dialog.CanStartEnvironment);
                switch ((kind, fix))
                {
                    case ("bmt", "uncheck"):
                        dialog.EnableBeatorajaBmtOutput = false;
                        await dialog.CommitBmtSettingsAsync();
                        Assert.IsFalse(values.EnableBeatorajaBmtOutput);
                        break;
                    case ("score", "uncheck"):
                        dialog.UseBeatorajaScoreDb = false;
                        await dialog.CommitScoreSettingsAsync();
                        Assert.IsFalse(values.UseBeatorajaScoreDb);
                        break;
                    case ("score", "repair-reopen"):
                        File.WriteAllBytes(values.BeatorajaScoreDbPath, []);
                        dialog.SetPresentationActive(false);
                        dialog.SetPresentationActive(true);
                        Assert.IsTrue(values.UseBeatorajaScoreDb);
                        break;
                    case ("image", "uncheck"):
                        dialog.UseExternalPanelImage = false;
                        Assert.IsFalse(values.UseExternalPanelImage);
                        break;
                    case ("image", "select"):
                        {
                            string image = Path.Combine(directory.Path, "stagefile.png");
                            File.WriteAllBytes(image, []);
                            dialog.StagefilePath = image;
                            Assert.IsTrue(values.UseExternalPanelImage);
                            Assert.AreEqual(image, values.StagefilePath);
                            break;
                        }
                    default: throw new ArgumentOutOfRangeException(nameof(fix));
                }
                Assert.AreEqual(0, dialog.Deficiencies.Count, SettingsDeficiency.FormatDiagnostic(dialog.Deficiencies));
                Assert.AreEqual(string.Empty, dialog.FieldReasons[reasonField.ToString()]);
                Assert.AreEqual(string.Empty, dialog.FieldReasons[listed.ToString()]);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally { dialog.SetPresentationActive(false); }
        }, nameof(SavedUnmetPairs_AreListedWithFieldReasonAndClearAfterEitherFix));
    }

    /// <summary>
    /// 有効な状態で表示した後に画面外で外部画像を失い、設定画面から明示開始すると、起動時検査が作った一覧をそのまま公開し、
    /// 警告が <c>Msg_init_settings_check</c> と一覧順の行になり、開始ボタンが無効になることを確認します（K3・K5）。
    /// </summary>
    /// <remarks>明示開始の起動時検査の終端で一覧を公開し忘れる誤り、警告と一覧の不一致、不備のまま構築へ進む誤りを検出します。</remarks>
    [TestMethod]
    public void ExplicitStartAfterExternalChange_PublishesStartupDeficienciesToGuidance()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            try
            {
                string path = Path.Combine(root, "user.config");
                Settings values = CreateValidStandaloneSettings(root, TestSettingsFactory.OpenSettings(path));
                string image = Path.Combine(root, "stagefile.png");
                File.WriteAllBytes(image, []);
                values.UseExternalPanelImage = true;
                values.StagefilePath = image;
                values.Save();
                var applicationPath = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "application", "BeMusicSeeker.exe"));
                var dialogs = new RecordingRootDialogService();
                var factory = new StartupLibraryConstructionTestSupport.NullStartupLibraryFactory();
                var composition = new ApplicationComposition(
                    settingsEditSession: new SettingsEditSession(values),
                    uiScheduler: new WpfUiScheduler(() => System.Windows.Threading.Dispatcher.CurrentDispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(true),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(), runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(applicationPath),
                    settingsDialogService: dialogs, fileDbMutationDialogService: dialogs,
                    dialogService: UndeclaredDialogs.Create());
                MainWindowViewModel owner = new(composition, factory);
                var sequence = new List<string>();
                owner.SettingDialog.AttachPresentationPort(new RecordingSettingsDialogPresentationPort(sequence.Add));
                dialogs.MessageObserved = () => sequence.Add("warning");
                try
                {
                    SettingsDialogViewModel dialog = owner.SettingDialog;
                    Assert.AreEqual(0, dialog.Deficiencies.Count);
                    Assert.IsTrue(dialog.IsInitialEnvironment);
                    File.Delete(image);
                    Assert.AreEqual(0, dialog.Deficiencies.Count, "画面外の変更は次の時機まで反映しません。");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.StartConfirmedEnvironmentAsync(), "deficiency-explicit-start");
                    CollectionAssert.AreEqual(new[] { "warning" }, sequence);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    CollectionAssert.AreEqual(new[] { (SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage) },
                        dialog.Deficiencies.Select(item => (item.Category, item.Field)).ToArray());
                    Assert.AreEqual(string.Join(Environment.NewLine, Resources.Msg_init_settings_check,
                        string.Format(CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, Resources.Appearance, Resources.Error_InvalidStagefilePath)),
                        dialogs.LastMessageText);
                    Assert.IsTrue(dialog.HasDeficiencies);
                    Assert.IsFalse(dialog.CanStartEnvironment);
                    Assert.IsFalse(owner.IsInitializationCompleted);
                    Assert.AreEqual(0, factory.LibraryCallCount);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "deficiency-explicit-start-owner-terminal");
                    owner.SettingDialog.Dispose();
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    /// <summary>
    /// 確定値由来の理由が出ているBMSディレクトリ欄（スタンドアロンの空の一覧、LR2の親子関係）でBMS候補を追加すると、欄の理由は確定前の案内だけを <c>Warning</c> で示し、
    /// 下部の一覧は同じインスタンスのままであること、候補を戻す編集・取消・確定のそれぞれで確定値由来の理由と <c>Error</c> に戻る（確定では不備が消える）ことを確認します。
    /// </summary>
    /// <remarks>未確定の候補を一覧へ混ぜる誤り、未確定の間に確定値由来の理由を残す誤り、候補が確定一覧に戻った後も案内を残す誤りを検出します。</remarks>
    [TestMethod]
    [DataRow("standalone-empty")]
    [DataRow("lr2-nested")]
    public void BmsDirectoriesField_ShowsOnlyUnconfirmedGuidanceWhileCandidateDiffersAndRestoresConfirmedReason(string mode)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            bool standalone = mode == "standalone-empty";
            Settings values = standalone ? CreateStandalone(directory.Path) : CreateLr2(directory.Path);
            string key = nameof(SettingsDeficiencyField.BmsDirectories);
            if (standalone) { values.StandaloneBmsRootPaths = string.Empty; values.BMSRootPath = string.Empty; }
            else { AddNestedSearchRoot(values.LR2ConfigXmlPath, Path.Combine(directory.Path, "lr2", "Songs", "Child")); }
            values.Save();
            string added = Directory.CreateDirectory(Path.Combine(directory.Path, "Added")).FullName;
            using var harness = SettingsDialogHarness.Create(values);
            SettingsDialogViewModel dialog = harness.Dialog;
            IReadOnlyList<SettingsDeficiency> initial = dialog.Deficiencies;
            string confirmedReason = dialog.FieldReasons[key];
            Assert.AreEqual("Error", dialog.FieldReasonStatuses[key]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(confirmedReason));
            Assert.IsTrue(initial.Any(item => item.Field == SettingsDeficiencyField.BmsDirectories));

            async Task StageCandidate()
            {
                await dialog.AddBmsSearchRootPathsAsync([added]);
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, added);
                Assert.AreEqual(Resources.Settings_bms_candidate_unconfirmed, dialog.FieldReasons[key]);
                Assert.AreEqual("Warning", dialog.FieldReasonStatuses[key]);
                Assert.AreSame(initial, dialog.Deficiencies, "下部の一覧は確定値だけから作り、BMS候補を読みません。");
            }

            await StageCandidate();
            await dialog.RemoveBmsSearchRootPathAsync(added);
            Assert.AreEqual(confirmedReason, dialog.FieldReasons[key], "候補を確定一覧へ戻す編集で確定値由来の理由に戻ります。");
            Assert.AreEqual("Error", dialog.FieldReasonStatuses[key]);

            await StageCandidate();
            dialog.CancelBmsRoots();
            Assert.AreEqual(confirmedReason, dialog.FieldReasons[key], "取消で確定値由来の理由に戻ります。");
            Assert.AreEqual("Error", dialog.FieldReasonStatuses[key]);

            if (standalone)
            {
                // 確定すると候補が確定一覧になり、不備が解消します。
                await StageCandidate();
                await dialog.CommitBmsRootsAsync();
                Assert.AreEqual(string.Empty, dialog.FieldReasons[key]);
                Assert.AreEqual(0, dialog.Deficiencies.Count, SettingsDeficiency.FormatDiagnostic(dialog.Deficiencies));
            }
        }, nameof(BmsDirectoriesField_ShowsOnlyUnconfirmedGuidanceWhileCandidateDiffersAndRestoresConfirmedReason));
    }

    /// <summary>
    /// 確定済みのLR2設定XMLを解決できないLR2連携では欄の直下に「LR2ディレクトリを設定すると編集できます。」を出し、解決できる場合・スタンドアロン・
    /// 解決できるが設定操作の進行中で欄が無効な場合には出さないことを確認します。
    /// </summary>
    /// <remarks>解決できる場合や操作中の無効化に案内を出す誤り、解決できない場合に案内を出さない誤りを検出します。</remarks>
    [TestMethod]
    [DataRow("lr2-unresolved", true, false)]
    [DataRow("lr2-resolved", false, true)]
    [DataRow("standalone", false, true)]
    [DataRow("lr2-resolved-operation-running", false, false)]
    public void BmsDirectoriesField_GuidesToLr2DirectoryOnlyWhileConfirmedLr2ConfigIsUnresolved(string kind, bool expectGuidance, bool expectEnabled)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = kind == "standalone" ? CreateStandalone(directory.Path) : CreateLr2(directory.Path);
            if (kind == "lr2-unresolved") { File.WriteAllText(values.LR2ConfigXmlPath, "<config><broken>"); }
            values.Save();
            using var harness = SettingsDialogHarness.Create(values);
            SettingsDialogViewModel dialog = harness.Dialog;
            string key = nameof(SettingsDeficiencyField.BmsDirectories);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? running = kind == "lr2-resolved-operation-running" ? dialog.RunSettingsOperationAsync(() => release.Task) : null;
            try
            {
                Assert.AreEqual(expectEnabled, dialog.IsBmsSearchRootEditorEnabled);
                Assert.AreEqual(expectGuidance, dialog.FieldReasons[key].Contains(Resources.Settings_bms_editor_requires_lr2_root, StringComparison.Ordinal), dialog.FieldReasons[key]);
            }
            finally
            {
                release.TrySetResult();
                if (running != null) { await running; }
            }
        }, nameof(BmsDirectoriesField_GuidesToLr2DirectoryOnlyWhileConfirmedLr2ConfigIsUnresolved));
    }

    /// <summary>
    /// 設定画面ではLR2設定を解決できる確定値で、起動時の検査が設定XMLを解決できなかったと報告すると、次に一覧を作り直すまでLR2環境の行を出し、
    /// 理由は保存済みのLR2ディレクトリが空なら「LR2ディレクトリを設定してください。」、空でなければ従来の文言になることを確認します。
    /// その間は出力先・導入先の行と、BMSディレクトリを追加して確定する行を出さず、通常・ROOT出力先と導入先の欄の直下の検証表示も出さず、
    /// 前提と無関係な外観・バックアップの行は出します。続く通常の作り直しでは、LR2環境の行が消え、抑止していた行が戻ります。
    /// </summary>
    /// <remarks>
    /// 起動時の検査の失敗を一覧へ渡さない誤り、理由の選択を保存値と食い違わせる誤り、抑止の漏れ・抑止し過ぎ、
    /// 通常の作り直しの後も起動時の検査の失敗を残す誤りを検出します。設定画面側でも解決に失敗する確定値では起動時の報告と区別できないため、解決できる確定値で組みます。
    /// </remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void StartupUnresolvedLr2Config_ListsLr2EnvironmentAndSuppressesDependentRowsUntilNextRefresh(bool lr2RootEmpty, bool noRegisteredRoot)
    {
        RunOnUiDispatcher(() =>
        {
            using var directory = new CaseRoot(CreateTemporaryRoot(CaseWorkspace));
            Settings values = CreateLr2(directory.Path);
            string missing = Path.Combine(directory.Path, "missing");
            if (lr2RootEmpty) { values.LR2RootPath = string.Empty; }
            if (noRegisteredRoot) { RemoveAllSearchRoots(values.LR2ConfigXmlPath); }
            // 抑止と復帰の差が観測できるように、出力先を未設定に、導入先を不備にし、前提と無関係なページの不備も置きます。
            values.LR2CustomFolderOutputBaseDir = string.Empty;
            values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            values.BMSInstallDir = missing;
            values.UseExternalPanelImage = true;
            values.StagefilePath = Path.Combine(missing, "stagefile.png");
            values.IsLR2BackupEnabled = true;
            values.LR2BackupPath = missing;
            values.Save();
            using var harness = SettingsDialogHarness.Create(values);
            SettingsDialogViewModel dialog = harness.Dialog;
            string[] dependentFields = [nameof(SettingsDeficiencyField.NormalOutput), nameof(SettingsDeficiencyField.RootOutput), nameof(SettingsDeficiencyField.InstallDirectory)];

            var normal = new List<(SettingsCategory Category, SettingsDeficiencyField Field, string Reason)>();
            if (noRegisteredRoot) { normal.Add((SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, Resources.Error_BmsDirectoryAndInstallRequired)); }
            normal.Add((SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath));
            normal.Add((SettingsCategory.Playlist, SettingsDeficiencyField.NormalOutput, Resources.Error_CustomFolderOutputPathNotSet));
            normal.Add((SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput, Resources.Error_CustomFolderRootOutputPathNotSet));
            if (!noRegisteredRoot) { normal.Add((SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDirNotRegistered)); }
            normal.Add((SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput, Resources.Error_LR2BackupPathNotSet));
            void AssertList(IEnumerable<(SettingsCategory Category, SettingsDeficiencyField Field, string Reason)> expected, IReadOnlyList<SettingsDeficiency> actual)
                => CollectionAssert.AreEqual(expected.ToArray(), actual.Select(item => (item.Category, item.Field, item.Reason)).ToArray(),
                    SettingsDeficiency.FormatDiagnostic(actual));

            // 前提：設定画面側では同じ確定値からLR2設定を解決でき、表示開始時の一覧にLR2環境の行はありません。
            AssertList(normal, dialog.Deficiencies);
            Assert.IsFalse(dialog.IsLr2OutputPrerequisiteMissing);

            IReadOnlyList<SettingsDeficiency> startup = dialog.RefreshStartupDeficiencies(lr2ConfigResolved: false);
            string lr2Reason = lr2RootEmpty ? Resources.Error_Lr2RootPathNotSet : Resources.Error_InvalidLR2SongDbOrConfigPath;
            AssertList(
            [
                (SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, lr2Reason),
                (SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath),
                (SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput, Resources.Error_LR2BackupPathNotSet)
            ], startup);
            Assert.AreSame(startup, dialog.Deficiencies);
            Assert.IsFalse(dialog.CanStartEnvironment);
            foreach (string field in dependentFields)
            {
                Assert.AreEqual(string.Empty, dialog.FieldReasons[field], field);
                Assert.AreEqual(string.Empty, dialog.FieldReasonStatuses[field], field);
            }

            // 次の通常の作り直しで、起動時の検査の失敗を手放し、確定値からの生成結果に戻ります。
            AssertList(normal, dialog.RefreshDeficiencies());
            Assert.AreEqual(Resources.Error_CustomFolderOutputPathNotSet, dialog.FieldReasons[nameof(SettingsDeficiencyField.NormalOutput)]);
            Assert.AreEqual(Resources.Error_CustomFolderRootOutputPathNotSet, dialog.FieldReasons[nameof(SettingsDeficiencyField.RootOutput)]);
            Assert.AreEqual(noRegisteredRoot ? string.Empty : Resources.Error_InvalidBmsInstallDirNotRegistered,
                dialog.FieldReasons[nameof(SettingsDeficiencyField.InstallDirectory)]);
            return Task.CompletedTask;
        }, nameof(StartupUnresolvedLr2Config_ListsLr2EnvironmentAndSuppressesDependentRowsUntilNextRefresh));
    }

    /// <summary>ケース専有の一時rootを、ケースの全Taskの終端後に回収します。</summary>
    private sealed record CaseRoot(string Path) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
        }
    }

    private static Settings CreateLr2(string root)
    {
        Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
        values.BeatorajaRootPath = string.Empty;
        values.IsLR2BackupEnabled = false;
        return values;
    }

    private static Settings CreateStandalone(string root)
    {
        string library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        Settings values = CreateValidStandaloneSettings(library, TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
        values.BeatorajaRootPath = string.Empty;
        return values;
    }

    /// <summary>旧版で保存され得た親子関係の検索ルートを、登録時の重なり検査を通さずに設定XMLへ書きます。</summary>
    private static void AddNestedSearchRoot(string configPath, string child)
    {
        Directory.CreateDirectory(child);
        var document = XDocument.Load(configPath);
        XElement jukebox = document.Root?.Element("jukebox") ?? throw new AssertFailedException("LR2設定XMLに jukebox がありません。");
        jukebox.Add(new XElement("path", child.TrimEnd('\\') + "\\"));
        document.Save(configPath);
    }

    /// <summary>LR2設定XMLから検索ルートの登録をすべて除きます。</summary>
    private static void RemoveAllSearchRoots(string configPath)
    {
        var document = XDocument.Load(configPath);
        XElement jukebox = document.Root?.Element("jukebox") ?? throw new AssertFailedException("LR2設定XMLに jukebox がありません。");
        jukebox.Elements("path").Remove();
        document.Save(configPath);
    }

    private static string CreateBeatorajaRoot(string parent, string name)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), "{}");
        string player = Directory.CreateDirectory(Path.Combine(root, "player", "player")).FullName;
        File.WriteAllBytes(Path.Combine(player, "score.db"), []);
        return root;
    }

    /// <summary>UIスレッドで非同期本体を終端まで実行し、変更した表示言語を元へ戻します。</summary>
    private static void RunOnUiDispatcher(Func<Task> operation, string operationName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CultureInfo? previousCulture = Resources.Culture;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation(), operationName);
            }
            finally
            {
                if (previousCulture is null) { Resources.Culture = null; }
                else { ResourceService.Current.ChangeCulture(previousCulture.Name); }
            }
        });
    }
}
