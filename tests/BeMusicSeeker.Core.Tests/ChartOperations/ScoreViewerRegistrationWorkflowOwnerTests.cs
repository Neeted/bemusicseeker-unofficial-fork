using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ScoreViewerRegistrationWorkflowOwnerTests
{
    private static readonly string Md5A = new('a', 32);
    private static readonly string Md5B = new('b', 32);
    private static readonly string Md5C = new('c', 32);
    private static readonly string Md5D = new('d', 32);

    /// <summary>登録状態のOK・拒否・nullと型不正、uploadの応答hashを区別し、非OKを成功として受理する誤りを検出します。</summary>
    [TestMethod]
    public void AppGatewayJsonParsers_PreserveStatusAndUploadContracts()
    {
        Assert.IsTrue(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"OK\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":\"REJECTED\"}"));
        Assert.IsFalse(AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
            "{\"status\":null}"));
        Assert.ThrowsExactly<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{\"status\":1}"));

        ScoreViewerUploadResponse accepted = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"OK\",\"md5\":\"server-hash\"}");
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual("server-hash", accepted.Hash);
        Assert.AreEqual("OK", accepted.Status);

        ScoreViewerUploadResponse rejected = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":\"REJECTED\"}");
        Assert.IsFalse(rejected.Accepted);
        Assert.AreEqual("REJECTED", rejected.Status);
        Assert.IsNull(rejected.Hash);

        ScoreViewerUploadResponse nullStatus = AppScoreViewerRegistrationGateway.ParseUploadResponse(
            "{\"status\":null}");
        Assert.IsFalse(nullStatus.Accepted);
        Assert.AreEqual(string.Empty, nullStatus.Status);
    }

    /// <summary>必須status・成功時md5の欠落と連結JSONを拒否し、不完全な応答を正常扱いする誤りを検出します。</summary>
    [TestMethod]
    public void AppGatewayJsonParsers_RejectMissingPropertiesAndTrailingDocuments()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse("{}"));
        Assert.ThrowsExactly<FormatException>(() =>
            AppScoreViewerRegistrationGateway.ParseUploadResponse("{\"status\":\"OK\"}"));
        Assert.ThrowsExactly<JsonReaderException>(() =>
            AppScoreViewerRegistrationGateway.ParseRegistrationStatusResponse(
                "{\"status\":\"OK\"} {\"status\":\"OK\"}"));
    }

    /// <summary>
    /// 登録済みの1行を開くとき、確認・アップロード・通知なしでそのままビューアを`?md5=`付きのURLで開き、情報メッセージも出さないことを確認します。
    /// 登録済みの一件へ複数対象用の案内を出す誤りと、登録済みなのに確認・アップロードする誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Open_AlreadyRegistered_OpensWithoutConfirmationUploadOrNotice()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new() { Gateway = { RegistrationStatus = _ => true } };
        ScoreViewerCandidate candidate = CreateOwned(workspace, Md5A, "Registered");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(candidate);

        Assert.IsNotNull(result);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.AlreadyRegistered, result.Items.Single().Status);
        CollectionAssert.AreEqual(new[] { Md5A }, harness.Gateway.StatusHashes);
        Assert.AreEqual(0, harness.Gateway.UploadPaths.Count);
        Assert.AreEqual(0, harness.Dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, harness.Dialogs.MessageRequests.Count, "情報メッセージを出さない。");
        AssertOpenedMd5(harness, Md5A);
    }

    /// <summary>
    /// 未登録の1行を開くとき、設定が有効なら1件用の確認（`Msg_show_chart`・タイトル・MD5を含む）をしてからアップロードして成功通知の後に開き、
    /// 設定が無効なら確認なしで同じ処理を行うことを確認します。
    /// 設定を無視して常に確認する誤り・確認しない誤り、確認が登録用の文言になる誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "設定有効は1件用の確認をする")]
    [DataRow(false, DisplayName = "設定無効は確認しない")]
    public async Task Open_Unregistered_ConfirmsPerSettingThenUploadsAndOpens(bool showSingleConfirmation)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new(showSingleConfirmation);
        ScoreViewerCandidate candidate = CreateOwned(workspace, Md5A, "Chart A");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(candidate);

        Assert.IsNotNull(result);
        if (showSingleConfirmation)
        {
            UiConfirmationRequest request = harness.Dialogs.ConfirmationRequests.Single();
            StringAssert.Contains(request.MessageBoxText, Resources.Msg_show_chart);
            StringAssert.Contains(request.MessageBoxText, "Chart A");
            StringAssert.Contains(request.MessageBoxText, Md5A);
            Assert.IsFalse(request.MessageBoxText.Contains(Resources.Msg_register_chart, StringComparison.Ordinal));
        }
        else
        {
            Assert.AreEqual(0, harness.Dialogs.ConfirmationRequests.Count);
        }
        CollectionAssert.AreEqual(new[] { candidate.Path }, harness.Gateway.UploadPaths);
        CollectionAssert.AreEqual(
            new[] { Resources.Msg_success_register_chart },
            harness.Dialogs.MessageRequests.Select(request => request.MessageBoxText).ToArray());
        AssertOpenedMd5(harness, Md5A);
    }

    /// <summary>
    /// 未登録の1行を開く確認で「いいえ」を選ぶと、アップロードも追加の通知もビューアを開くこともしないことを確認します。
    /// 拒否後にそのまま開く誤り・アップロードする誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Open_ConfirmationDeclined_DoesNotUploadNotifyOrOpen()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new();
        harness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(CreateOwned(workspace, Md5A, "Chart A"));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.HasUploadDeclined);
        Assert.AreEqual(1, harness.Dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, harness.Gateway.UploadPaths.Count);
        Assert.AreEqual(0, harness.Dialogs.MessageRequests.Count);
        Assert.AreEqual(0, harness.Shell.Requests.Count);
    }

    /// <summary>
    /// ファイルのない1行（実ファイルなし・パスなし）を開くとき、照会・アップロード・確認をせず、MD5だけでビューアを開くことを確認します。
    /// 未所持の行で不要な通信や確認をする誤りと、開けない誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "パスはあるがファイルが実在しない")]
    [DataRow(true, DisplayName = "パスがない")]
    public async Task Open_WithoutFile_OpensByMd5WithoutInquiryUploadOrConfirmation(bool noPath)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new();
        var candidate = new ScoreViewerCandidate(
            ChartFileKind.Bms,
            Md5A,
            noPath ? null! : workspace.GetPath("missing.bms"),
            "Missing");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(candidate);

        Assert.IsNotNull(result);
        Assert.AreEqual(ScoreViewerRegistrationItemStatus.HashOnly, result.Items.Single().Status);
        Assert.AreEqual(0, harness.Gateway.StatusHashes.Count);
        Assert.AreEqual(0, harness.Gateway.UploadPaths.Count);
        Assert.AreEqual(0, harness.Dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, harness.Dialogs.MessageRequests.Count);
        AssertOpenedMd5(harness, Md5A);
    }

    /// <summary>
    /// 1行を開く操作で、照会の失敗とアップロードの失敗のどちらでもビューアを開かず、失敗の通知だけを出すことを確認します。
    /// 失敗したのに開く誤りと、失敗を通知しない誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "照会が失敗")]
    [DataRow(false, DisplayName = "アップロードが失敗")]
    public async Task Open_StatusOrUploadFailure_DoesNotOpenAndNotifiesFailure(bool statusFails)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new(showSingleConfirmation: false);
        harness.Gateway.RegistrationStatus = _ => statusFails ? throw new IOException("status failed") : false;
        harness.Gateway.UploadResult = _ => ScoreViewerUploadResponse.Rejected("REJECTED");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(CreateOwned(workspace, Md5A, "Chart A"));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.HasFailures);
        Assert.AreEqual(0, harness.Shell.Requests.Count);
        CollectionAssert.AreEqual(
            new[] { Resources.Error_ScoreViewerRegistrationPartialFailure },
            harness.Dialogs.MessageRequests.Select(request => request.MessageBoxText).ToArray());
    }

    /// <summary>
    /// 2行以上の登録で、新しく登録する件数Nが1以上なら、設定によらず（設定無効・N=1でも）Nを示す複数登録の確認（`Msg_register_chart`と件数）を出し、
    /// 1件用の確認の文言を出さず、所持BMSだけを照会・アップロードし、ビューアを開かないことを確認します。
    /// 確認の件数が登録対象の総数（登録済みを含む）になる誤り、登録で設定により確認が消える誤り、ファイルのない対象・BMSONを照会する誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, 1, DisplayName = "設定無効でも新規1件で確認する")]
    [DataRow(true, 1, DisplayName = "設定有効で新規1件で確認する")]
    [DataRow(false, 2, DisplayName = "設定無効でも新規2件で確認する")]
    [DataRow(true, 2, DisplayName = "設定有効で新規2件で確認する")]
    public async Task Register_ConfirmsNewRegistrationCountRegardlessOfSettingAndInquiresOwnedBmsOnly(
        bool showSingleConfirmation,
        int newCount)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new(showSingleConfirmation);
        harness.Gateway.RegistrationStatus = hash => hash == Md5D;
        ScoreViewerCandidate registered = CreateOwned(workspace, Md5D, "Registered");
        ScoreViewerCandidate[] newCharts =
        [
            CreateOwned(workspace, Md5A, "New A"),
            CreateOwned(workspace, Md5B, "New B"),
        ];
        var fileless = new ScoreViewerCandidate(ChartFileKind.Bms, Md5C, workspace.GetPath("missing.bms"), "Fileless");
        var bmson = new ScoreViewerCandidate(ChartFileKind.Bmson, new string('e', 32), CreateFile(workspace, "chart.bmson"), "Bmson");
        ScoreViewerCandidate[] selection = [registered, .. newCharts.Take(newCount), fileless, bmson, ScoreViewerCandidate.NotTarget];

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(selection);

        Assert.IsNotNull(result);
        UiConfirmationRequest request = harness.Dialogs.ConfirmationRequests.Single();
        StringAssert.Contains(request.MessageBoxText, Resources.Msg_register_chart);
        StringAssert.Contains(request.MessageBoxText, newCount + " " + Resources.Num_chart);
        Assert.IsFalse(request.MessageBoxText.Contains(Resources.Msg_show_chart, StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { Md5D }.Concat(newCharts.Take(newCount).Select(chart => chart.Md5)).ToArray(),
            harness.Gateway.StatusHashes.ToArray(),
            "照会は所持BMSだけ。");
        CollectionAssert.AreEquivalent(
            newCharts.Take(newCount).Select(chart => chart.Path).ToArray(),
            harness.Gateway.UploadPaths.ToArray());
        CollectionAssert.AreEqual(
            new[] { Resources.Msg_success_register_chart },
            harness.Dialogs.MessageRequests.Select(message => message.MessageBoxText).ToArray());
        Assert.AreEqual(0, harness.Shell.Requests.Count, "登録ではビューアを開かない。");
    }

    /// <summary>
    /// 2行以上の登録で新しく登録する件数が0（全件登録済み、または登録済みとファイルなしの混在）なら、確認せず、
    /// ファイルのない対象を照会せず、新しく登録する譜面がない旨の情報を一回だけ通知し、ビューアを開かないことを確認します。
    /// 確認を出す誤り、ファイルのない対象を照会する誤り、何も通知しない誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "全件登録済み")]
    [DataRow(true, DisplayName = "登録済みとファイルなしの混在")]
    public async Task Register_WithoutNewRegistration_ShowsSingleInformationWithoutConfirmation(bool withFileless)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new() { Gateway = { RegistrationStatus = _ => true } };
        ScoreViewerCandidate first = CreateOwned(workspace, Md5A, "A");
        ScoreViewerCandidate second = withFileless
            ? new ScoreViewerCandidate(ChartFileKind.Bms, Md5B, workspace.GetPath("missing.bms"), "Fileless")
            : CreateOwned(workspace, Md5B, "B");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(first, second);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, harness.Dialogs.ConfirmationRequests.Count);
        CollectionAssert.AreEqual(
            new[] { Resources.Msg_score_viewer_no_new_registration },
            harness.Dialogs.MessageRequests.Select(request => request.MessageBoxText).ToArray());
        CollectionAssert.AreEqual(
            withFileless ? new[] { Md5A } : new[] { Md5A, Md5B },
            harness.Gateway.StatusHashes);
        Assert.AreEqual(0, harness.Gateway.UploadPaths.Count);
        Assert.AreEqual(0, harness.Shell.Requests.Count);
    }

    /// <summary>
    /// 2行以上の登録で、全件アップロード成功なら成功の通知だけ、全件失敗なら失敗の通知だけ、混在なら成功と失敗の両方、
    /// 確認で「いいえ」ならアップロードも追加の通知もなく、いずれもビューアを開かないことを確認します。
    /// 成功と失敗の通知の取り違え・混在での片方の欠落、辞退後の通知、登録でビューアを開く誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(RegisterOutcome.AllUploaded, DisplayName = "全件成功は成功だけ")]
    [DataRow(RegisterOutcome.AllFailed, DisplayName = "全件失敗は失敗だけ")]
    [DataRow(RegisterOutcome.Mixed, DisplayName = "成功と失敗の混在は両方")]
    [DataRow(RegisterOutcome.Declined, DisplayName = "いいえはアップロードも通知もなし")]
    public async Task Register_NotifiesOnlyWhatHappened(RegisterOutcome outcome)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new();
        ScoreViewerCandidate first = CreateOwned(workspace, Md5A, "A");
        ScoreViewerCandidate second = CreateOwned(workspace, Md5B, "B");
        harness.Gateway.UploadResult = path => outcome switch
        {
            RegisterOutcome.AllUploaded => ScoreViewerUploadResponse.Success(Md5A),
            RegisterOutcome.AllFailed => ScoreViewerUploadResponse.Rejected("REJECTED"),
            _ => path == first.Path ? ScoreViewerUploadResponse.Success(Md5A) : ScoreViewerUploadResponse.Rejected("REJECTED"),
        };
        if (outcome == RegisterOutcome.Declined)
        {
            harness.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
        }

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(first, second);

        Assert.IsNotNull(result);
        string[] expected = outcome switch
        {
            RegisterOutcome.AllUploaded => [Resources.Msg_success_register_chart],
            RegisterOutcome.AllFailed => [Resources.Error_ScoreViewerRegistrationPartialFailure],
            RegisterOutcome.Mixed =>
                [Resources.Msg_success_register_chart, Resources.Error_ScoreViewerRegistrationPartialFailure],
            _ => [],
        };
        CollectionAssert.AreEqual(
            expected,
            harness.Dialogs.MessageRequests.Select(request => request.MessageBoxText).ToArray());
        Assert.AreEqual(outcome == RegisterOutcome.Declined ? 0 : 2, harness.Gateway.UploadPaths.Count);
        Assert.AreEqual(0, harness.Shell.Requests.Count, "登録ではビューアを開かない。");
    }

    /// <summary>
    /// 同じMD5の所持BMSを2件選んで登録するとき、重複を畳まず2件を照会・アップロードし、確認の件数も2件になることを確認します。
    /// 重複を畳んで件数や処理を減らす誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Register_DuplicateMd5_IsNotFolded()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new();

        await harness.ClickAsync(CreateOwned(workspace, Md5A, "A1"), CreateOwned(workspace, Md5A, "A2"));

        CollectionAssert.AreEqual(new[] { Md5A, Md5A }, harness.Gateway.StatusHashes);
        Assert.AreEqual(2, harness.Gateway.UploadPaths.Count);
        StringAssert.Contains(harness.Dialogs.ConfirmationRequests.Single().MessageBoxText, "2 " + Resources.Num_chart);
    }

    /// <summary>
    /// 表示後にファイルの有無や選択が変わってから処理口を呼ぶと、表示時の判定を使い回さず、クリック時の候補から判定し直すことを確認します。
    /// 開く対象のファイルが消えたらMD5だけで開き、登録の所持BMSが1件だけ消えたら残りだけを照会し、両方消えたら確認も通知も照会もしません。
    /// 表示時の判定結果をクリック時へ使い回す誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Click_AfterFileDeleted_RedecidesFromTheCurrentCandidates()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ScoreViewerCandidate single = CreateOwned(workspace, Md5A, "Single");
        {
            Harness open = new();
            Assert.AreEqual(ScoreViewerMenuMode.Open, open.Terminal.ResolveMenuMode([single]));
            File.Delete(single.Path);

            await open.ClickAsync(single);

            Assert.AreEqual(0, open.Gateway.StatusHashes.Count, "ファイルが消えたのでMD5だけで開く。");
            Assert.AreEqual(0, open.Dialogs.ConfirmationRequests.Count);
            AssertOpenedMd5(open, Md5A);
        }

        ScoreViewerCandidate first = CreateOwned(workspace, Md5B, "First");
        ScoreViewerCandidate second = CreateOwned(workspace, Md5C, "Second");
        {
            Harness register = new();
            Assert.AreEqual(ScoreViewerMenuMode.Register, register.Terminal.ResolveMenuMode([first, second]));
            File.Delete(first.Path);

            await register.ClickAsync(first, second);

            CollectionAssert.AreEqual(new[] { Md5C }, register.Gateway.StatusHashes, "残った所持BMSだけを照会する。");
            Assert.AreEqual(0, register.Shell.Requests.Count);
        }

        Harness hidden = new();
        File.Delete(second.Path);

        ScoreViewerRegistrationResult? result = await hidden.ClickAsync(first, second);

        Assert.IsNull(result);
        Assert.AreEqual(0, hidden.Gateway.StatusHashes.Count);
        Assert.AreEqual(0, hidden.Dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, hidden.Dialogs.MessageRequests.Count);
        Assert.AreEqual(0, hidden.Shell.Requests.Count);
    }

    /// <summary>
    /// 判定が非表示になる選択（1行でBMSON）でクリックされても、照会・確認・通知・ビューア起動のいずれもしないことを確認します。
    /// 非表示のはずの操作が処理を始める誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Click_HiddenDecision_DoesNothing()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Harness harness = new();
        var bmson = new ScoreViewerCandidate(ChartFileKind.Bmson, Md5A, CreateFile(workspace, "chart.bmson"), "Bmson");

        ScoreViewerRegistrationResult? result = await harness.ClickAsync(bmson);

        Assert.IsNull(result);
        Assert.AreEqual(0, harness.Gateway.StatusHashes.Count);
        Assert.AreEqual(0, harness.Dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(0, harness.Dialogs.MessageRequests.Count);
        Assert.AreEqual(0, harness.Shell.Requests.Count);
    }

    /// <summary>登録済み・状態取得失敗・upload拒否を対象別に保持し、拒否したuploadの実行や失敗の消失を検出します。</summary>
    [TestMethod]
    public async Task RunAsync_PreservesRegisteredStatusFailureAndDeclinedUpload()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var gateway = new RecordingGateway
        {
            RegistrationStatus = hash => hash == Md5A
                ? true
                : hash == Md5C ? throw new IOException("status failed") : false,
        };
        var interaction = new RecordingInteraction { ConfirmationResult = false };
        var warnings = new List<(Exception? Exception, string Message)>();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction, warnings.Add);
        var decision = ScoreViewerMenuDecision.Resolve(
        [
            CreateOwned(workspace, Md5A, "Registered"),
            CreateOwned(workspace, Md5B, "Needs upload"),
            CreateOwned(workspace, Md5C, "Status failure"),
        ]);

        ScoreViewerRegistrationResult? result = await owner.RunAsync(decision, "mixed_preflight");

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(
            new[]
            {
                ScoreViewerRegistrationItemStatus.AlreadyRegistered,
                ScoreViewerRegistrationItemStatus.UploadDeclined,
                ScoreViewerRegistrationItemStatus.StatusCheckFailed
            },
            result.Items.Select(item => item.Status).ToArray());
        Assert.AreEqual(0, gateway.UploadPaths.Count);
        Assert.AreEqual(1, interaction.ConfirmationCount);
        Assert.IsTrue(result.HasFailures);
        Assert.IsTrue(result.HasUploadDeclined);
        Assert.AreEqual(1, warnings.Count(item => item.Message.Contains("score_viewer_status_failed")));
    }

    /// <summary>upload成功のserver hashと拒否・例外を個別に報告し、一件の失敗による後続欠落や成功扱いを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ReportsUploadSuccessRejectionAndExceptionPerTarget()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ScoreViewerCandidate success = CreateOwned(workspace, Md5A, "Success");
        ScoreViewerCandidate rejected = CreateOwned(workspace, Md5B, "Rejected");
        ScoreViewerCandidate failed = CreateOwned(workspace, Md5C, "Failed");
        var gateway = new RecordingGateway
        {
            UploadResult = path => path == success.Path
                ? ScoreViewerUploadResponse.Success("server-hash")
                : path == rejected.Path
                    ? ScoreViewerUploadResponse.Rejected("REJECTED")
                    : throw new IOException("upload failed")
        };
        var interaction = new RecordingInteraction();
        var warnings = new List<(Exception? Exception, string Message)>();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(gateway, interaction, warnings.Add);

        ScoreViewerRegistrationResult? result = await owner.RunAsync(
            ScoreViewerMenuDecision.Resolve([success, rejected, failed]),
            "upload_results");

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(
            new[]
            {
                ScoreViewerRegistrationItemStatus.Uploaded,
                ScoreViewerRegistrationItemStatus.UploadFailed,
                ScoreViewerRegistrationItemStatus.UploadFailed
            },
            result.Items.Select(item => item.Status).ToArray());
        Assert.AreEqual("server-hash", result.Items[0].Hash);
        Assert.AreEqual("Score Viewer upload status: REJECTED", result.Items[1].FailureMessage);
        Assert.AreEqual("upload failed", result.Items[2].FailureMessage);
        Assert.AreEqual(3, gateway.UploadPaths.Count);
        Assert.IsTrue(result.HasUploadedRegistration);
        Assert.IsTrue(result.HasFailures);
        Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_rejected")));
        Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_upload_failed")));
    }

    /// <summary>確認処理の例外を失敗表示へ渡してuploadを抑止し、確認失敗を成功として処理する誤りを検出します。</summary>
    [TestMethod]
    public async Task RunAsync_ConfirmationFailureUsesFailurePresentationAndDoesNotUpload()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var gateway = new RecordingGateway();
        var interaction = new RecordingInteraction
        {
            ConfirmationException = new InvalidOperationException("confirmation failed")
        };
        var warnings = new List<(Exception? Exception, string Message)>();
        ScoreViewerRegistrationWorkflowOwner owner = CreateOwner(
            gateway,
            interaction,
            warnings.Add,
            showSingleTargetConfirmation: false);

        ScoreViewerRegistrationResult? result = await owner.RunAsync(
            ScoreViewerMenuDecision.Resolve([CreateOwned(workspace, Md5A, "Target")]),
            "confirmation_failure");

        Assert.IsNull(result);
        Assert.IsFalse(interaction.LastShowSingleTargetConfirmation);
        Assert.AreEqual("confirmation failed", interaction.PresentedFailure?.Message);
        Assert.AreEqual(0, gateway.UploadPaths.Count);
        Assert.IsTrue(warnings.Any(item => item.Message.Contains("score_viewer_registration_failed")));
    }

    /// <summary>Yes・Noの返答と閉じる操作を確認要求の契約に従って解釈し、拒否を承認扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_MapsConfirmationChoicesAndRecordsTypedRequest()
    {
        var dialogs = new RecordingUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);
        ScoreViewerRegistrationPlan plan = CreateUploadPlan(ScoreViewerMenuMode.Open);

        Assert.IsTrue(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
        Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
        Assert.AreEqual(MessageBoxButton.YesNo, dialogs.ConfirmationRequests[0].Button);
        Assert.AreEqual(MessageBoxImage.Asterisk, dialogs.ConfirmationRequests[0].Icon);
        Assert.AreEqual(Resources.Confirm, dialogs.ConfirmationRequests[0].Caption);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No);
        Assert.IsFalse(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));

        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.Yes);
        Assert.IsTrue(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
        dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.No);
        Assert.IsFalse(await interaction.ConfirmUploadAsync(plan, showSingleTargetConfirmation: true));
    }

    /// <summary>
    /// 1件用の確認を無効にした設定は、開く操作では確認を表示せず承認し、登録する操作では新規が1件でも確認を表示することを対にして確認します。
    /// 設定を開くで無視する誤りと、登録で設定により確認が消える誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task WpfInteraction_SingleConfirmationSettingAppliesToOpenOnly()
    {
        var dialogs = new RecordingUiDialogService();
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);

        Assert.IsTrue(await interaction.ConfirmUploadAsync(
            CreateUploadPlan(ScoreViewerMenuMode.Open),
            showSingleTargetConfirmation: false));
        Assert.AreEqual(0, dialogs.ConfirmationRequests.Count, "開くは設定無効で確認しない。");

        Assert.IsTrue(await interaction.ConfirmUploadAsync(
            CreateUploadPlan(ScoreViewerMenuMode.Register),
            showSingleTargetConfirmation: false));
        UiConfirmationRequest request = dialogs.ConfirmationRequests.Single();
        StringAssert.Contains(request.MessageBoxText, Resources.Msg_register_chart);
        StringAssert.Contains(request.MessageBoxText, "1 " + Resources.Num_chart);
    }

    /// <summary>表示失敗・owner不在を例外として伝え、利用者の拒否へ読み替える誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_PreservesConfirmationDisplayFailures()
    {
        var dialogs = new RecordingUiDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("display failed"))
        };
        var interaction = new WpfScoreViewerRegistrationInteraction(
            (_, _) => { },
            new RecordingExternalShellGateway(),
            dialogs);

        InvalidOperationException failed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => interaction.ConfirmUploadAsync(CreateUploadPlan(ScoreViewerMenuMode.Open), showSingleTargetConfirmation: true));
        StringAssert.Contains(failed.Message, "dialog failed");

        dialogs.ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable);
        InvalidOperationException unavailable = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => interaction.ConfirmUploadAsync(CreateUploadPlan(ScoreViewerMenuMode.Open), showSingleTargetConfirmation: true));
        StringAssert.Contains(unavailable.Message, UiDialogStatus.OwnerUnavailable.ToString());
    }

    /// <summary>
    /// 新しく登録する譜面がない旨の通知の表示が失敗した場合に、既存の結果通知と同じく失敗通知へ渡り、通常終了として隠されないことを確認します。
    /// 前提は所持済みで登録済みの2件です。
    /// </summary>
    [TestMethod]
    public async Task RunAsync_NoNewRegistrationDisplayFailure_UsesFailurePresentation()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var interaction = new RecordingInteraction();
        var owner = new ScoreViewerRegistrationWorkflowOwner(
            new RecordingGateway { RegistrationStatus = _ => true },
            new ThrowingNoNewInteraction(interaction),
            () => true,
            (_, _) => { });

        ScoreViewerRegistrationResult? result = await owner.RunAsync(
            ScoreViewerMenuDecision.Resolve([CreateOwned(workspace, Md5A, "A"), CreateOwned(workspace, Md5B, "B")]),
            "display_failure");

        Assert.IsNull(result);
        Assert.AreEqual("no new display failed", interaction.PresentedFailure?.Message);
    }

    /// <summary>結果通知の表示失敗を呼出元へ伝え、通常終了として隠す誤りを検出します。</summary>
    [TestMethod]
    public async Task WpfInteraction_DoesNotTreatResultDisplayFailureAsUserRejection()
    {
        var dialogs = new RecordingUiDialogService
        {
            MessageResult = UiDialogResult.Failed(new InvalidOperationException("message failed"))
        };
        var interaction = new WpfScoreViewerRegistrationInteraction((_, _) => { }, new RecordingExternalShellGateway(), dialogs);
        var result = new ScoreViewerRegistrationResult([
            ScoreViewerRegistrationItem.Uploaded(
                new ScoreViewerTarget("hash", "chart.bms", "Chart"),
                "hash",
                "https://viewer/hash")]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => interaction.PresentResultAsync(result));
    }

    public enum RegisterOutcome
    {
        AllUploaded,
        AllFailed,
        Mixed,
        Declined,
    }

    private static string CreateFile(TestWorkspace workspace, string name)
    {
        string path = workspace.GetPath(name);
        File.WriteAllText(path, "#TITLE test");
        return path;
    }

    private static ScoreViewerCandidate CreateOwned(TestWorkspace workspace, string md5, string title)
    {
        return new ScoreViewerCandidate(
            ChartFileKind.Bms,
            md5,
            CreateFile(workspace, Guid.NewGuid().ToString("N") + ".bms"),
            title);
    }

    private static void AssertOpenedMd5(Harness harness, string md5)
    {
        ExternalShellRequest request = harness.Shell.Requests.Single();
        Assert.AreEqual(ExternalShellRequestKind.Url, request.Kind);
        Assert.AreEqual("?md5=" + md5, new Uri(request.Target).Query);
    }

    private static ScoreViewerRegistrationWorkflowOwner CreateOwner(
        RecordingGateway gateway,
        RecordingInteraction interaction,
        Action<(Exception? Exception, string Message)>? warning = null,
        bool showSingleTargetConfirmation = true)
    {
        return new ScoreViewerRegistrationWorkflowOwner(
            gateway,
            interaction,
            () => showSingleTargetConfirmation,
            (exception, message) => warning?.Invoke((exception, message)));
    }

    private static ScoreViewerRegistrationPlan CreateUploadPlan(ScoreViewerMenuMode mode)
    {
        return new ScoreViewerRegistrationPlan(
            [
                ScoreViewerRegistrationItem.NeedsUpload(
                    new ScoreViewerTarget("hash", "chart.bms", "Chart"),
                    "hash")
            ],
            mode);
    }

    /// <summary>実物の表示窓口・担当・判定を記録用の窓口とゲートウェイで束ね、クリック時の処理口から通します。</summary>
    private sealed class Harness
    {
        internal Harness(bool showSingleConfirmation = true)
        {
            Owner = new ScoreViewerRegistrationWorkflowOwner(
                Gateway,
                new WpfScoreViewerRegistrationInteraction((_, _) => { }, Shell, Dialogs),
                () => showSingleConfirmation,
                (_, _) => { });
            Terminal = new MainWindowScoreViewerTerminal(Owner.RunAsync);
        }

        internal RecordingGateway Gateway { get; } = new();

        internal RecordingUiDialogService Dialogs { get; } = new();

        internal RecordingExternalShellGateway Shell { get; } = new();

        internal ScoreViewerRegistrationWorkflowOwner Owner { get; }

        internal MainWindowScoreViewerTerminal Terminal { get; }

        internal Task<ScoreViewerRegistrationResult?> ClickAsync(params ScoreViewerCandidate[] candidates)
            => Terminal.RunAsync(candidates, "test");
    }

    private sealed class ThrowingNoNewInteraction(RecordingInteraction inner) : IScoreViewerRegistrationInteraction
    {
        public Task<bool> ConfirmUploadAsync(ScoreViewerRegistrationPlan plan, bool showSingleTargetConfirmation)
            => inner.ConfirmUploadAsync(plan, showSingleTargetConfirmation);

        public Task PresentResultAsync(ScoreViewerRegistrationResult result) => inner.PresentResultAsync(result);

        public Task PresentNoNewRegistrationAsync() => Task.FromException(new InvalidOperationException("no new display failed"));

        public Task PresentFailureAsync(Exception exception) => inner.PresentFailureAsync(exception);

        public void OpenViewer(string url) => inner.OpenViewer(url);
    }

    private sealed class RecordingGateway : IScoreViewerRegistrationGateway
    {
        internal Func<string, bool> RegistrationStatus { get; set; } = _ => false;

        internal Func<string, ScoreViewerUploadResponse> UploadResult { get; set; } = _ => ScoreViewerUploadResponse.Success(string.Empty);

        internal List<string> StatusHashes { get; } = [];

        internal List<string> UploadPaths { get; } = [];

        public bool IsRegistered(string hash)
        {
            StatusHashes.Add(hash);
            return RegistrationStatus(hash);
        }

        public ScoreViewerUploadResponse Upload(string path)
        {
            UploadPaths.Add(path);
            return UploadResult(path);
        }
    }

    private sealed class RecordingInteraction : IScoreViewerRegistrationInteraction
    {
        internal bool ConfirmationResult { get; set; } = true;

        internal Exception? ConfirmationException { get; set; }

        internal int ConfirmationCount { get; private set; }

        internal bool LastShowSingleTargetConfirmation { get; private set; }

        internal ScoreViewerRegistrationResult? PresentedResult { get; private set; }

        internal int NoNewRegistrationCount { get; private set; }

        internal Exception? PresentedFailure { get; private set; }

        internal string? OpenedUrl { get; private set; }

        public Task<bool> ConfirmUploadAsync(
            ScoreViewerRegistrationPlan plan,
            bool showSingleTargetConfirmation)
        {
            ConfirmationCount++;
            LastShowSingleTargetConfirmation = showSingleTargetConfirmation;
            return ConfirmationException == null
                ? Task.FromResult(ConfirmationResult)
                : Task.FromException<bool>(ConfirmationException);
        }

        public Task PresentResultAsync(ScoreViewerRegistrationResult result)
        {
            PresentedResult = result;
            return Task.CompletedTask;
        }

        public Task PresentNoNewRegistrationAsync()
        {
            NoNewRegistrationCount++;
            return Task.CompletedTask;
        }

        public Task PresentFailureAsync(Exception exception)
        {
            PresentedFailure = exception;
            return Task.CompletedTask;
        }

        public void OpenViewer(string url)
        {
            OpenedUrl = url;
        }
    }

    private sealed class RecordingUiDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiConfirmationRequest> ConfirmationRequests { get; } = [];

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            ConfirmationRequests.Add(request);
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, System.Threading.CancellationToken cancellationToken = default)
            where TWindow : Window
            => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingExternalShellGateway : IExternalShellGateway
    {
        internal List<ExternalShellRequest> Requests { get; } = [];

        public void Open(ExternalShellRequest request) => Requests.Add(request);

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => new();

        public ExplorerOpenResult OpenDirectory(string directoryPath) => new();

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }
}
