using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

/// <summary>
/// 未宣言のダイアログ要求を共有ホストの観測記録の項目 <c>D</c> に残して失敗結果を返す窓口と、画面テストの構成の既定の接続を検証します。
/// </summary>
/// <remarks>
/// 自分の記録だけを <see cref="HostObservationLog.TakeCurrentCase"/> で消費します。他の由来の <c>D</c> は境界へ残し、ケースの失敗にさせます。
/// 記録はケースの直列実行による帰属を前提にするため、全ケースを <see cref="DoNotParallelizeAttribute"/> にします。
/// 型名の契約は FullName です。照合は単純名（ジェネリックの arity 記号を除く）を含むかで行い、表記の差を許容します。
/// </remarks>
[TestClass]
public sealed class UndeclaredUiDialogServiceTests
{
    private static readonly string[] Members =
    [
        nameof(IUiDialogService.ShowMessageAsync),
        nameof(IUiDialogService.ConfirmAsync),
        nameof(IUiDialogService.ShowWindowAsync),
        nameof(IUiDialogService.PickFileAsync),
        nameof(IUiDialogService.PickFolderAsync),
        nameof(IUiDialogService.PickSaveFileAsync),
        nameof(IUiDialogService.RunWithProgressAsync),
    ];

    /// <summary>
    /// 条件：境界が有効なmoduleで、七つのメンバーをOwner省略で呼び、題名と本文に改行を含めます。
    /// 期待結果：呼出しが戻った時点（Taskを待つ前）で、自分の <c>D</c> がちょうど一件あり、キーが規定の順に並び、題名・本文が規定の表記で、
    /// <c>owner=null</c> です。結果はFailedで原因の例外を持ち、Window生成・結果生成・進捗の処理は呼ばれません。
    /// 検出する誤り：一部のメンバーで記録しない・二重に記録する・待った後に記録する、題名と本文の取り違え、ピッカーの本文や
    /// <c>ShowWindowAsync</c> の題名・本文が <c>-</c> でない、要約しない、OwnerUnavailableや成功を返す、factoryやoperationを実行する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(nameof(IUiDialogService.ShowMessageAsync))]
    [DataRow(nameof(IUiDialogService.ConfirmAsync))]
    [DataRow(nameof(IUiDialogService.ShowWindowAsync))]
    [DataRow(nameof(IUiDialogService.PickFileAsync))]
    [DataRow(nameof(IUiDialogService.PickFolderAsync))]
    [DataRow(nameof(IUiDialogService.PickSaveFileAsync))]
    [DataRow(nameof(IUiDialogService.RunWithProgressAsync))]
    public async Task EachMember_RecordsOneDLineAtCallAndReturnsFailedWithoutRunningCallbacks(string member)
    {
        var service = new UndeclaredUiDialogService();
        var callbacks = new CallbackFlags();
        string tag = "t1-" + member;

        Invocation invocation = Call(member, service, null, tag, callbacks);
        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("D", invocation.IsMine);
        (UiDialogStatus status, Exception? error) = await invocation.Result;

        Assert.HasCount(1, records, "呼出しが戻った時点で、自分のDがちょうど一件あります。");
        var fields = Fields.Parse(records[0].Detail);
        AssertKeyOrder(fields, member);
        Assert.AreEqual(member, fields.Get("method"));
        StringAssert.Contains(fields.Get("request"), invocation.RequestTypeSimpleName);
        Assert.AreEqual(invocation.ExpectedTitle, fields.Get("title"));
        Assert.AreEqual(invocation.ExpectedText, fields.Get("text"));
        Assert.AreEqual("null", fields.Get("owner"));
        if (member == nameof(IUiDialogService.ShowWindowAsync)) { StringAssert.Contains(fields.Get("result"), nameof(WindowResultProbe)); }
        Assert.AreEqual(UiDialogStatus.Failed, status);
        Assert.IsNotNull(error, "表示に失敗した原因の例外を持ちます。");
        Assert.IsFalse(callbacks.Any, "Window生成・結果生成・進捗の処理は呼びません。");
    }

    /// <summary>
    /// 条件：共有ホストのスレッドで作った未表示のWindowを、七つのメンバーの要求のOwnerへ渡して呼びます。
    /// 期待結果：各メンバーの <c>D</c> が一件ずつあり、<c>owner</c> はそのWindowの型を含みます。<c>method</c> の集合は七つのメンバー名と一致します。
    /// WindowはClose後に <c>Application.Windows</c> から外れ、<c>MainWindow</c> は基準（null）に戻ります。
    /// 検出する誤り：ownerを常にnullにする、要求の型ごとのOwnerの取り出しを誤る・抜かす、要求のOwnerではなく解決後の所有者を書く。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task EachMember_RecordsTheRequestOwnerType()
    {
        var service = new UndeclaredUiDialogService();
        var callbacks = new CallbackFlags();
        List<Invocation> invocations = [];
        bool listedAfterClose = true;
        Window? mainWindowAfter = null;

        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current ?? throw new InvalidOperationException("The shared host application is unavailable.");
            Window? previousMain = application.MainWindow;
            var probe = new DialogOwnerProbeWindow();
            try
            {
                if (!ReferenceEquals(application.MainWindow, previousMain)) { application.MainWindow = previousMain; }
                foreach (string member in Members) { invocations.Add(Call(member, service, probe, "t2-" + member, callbacks)); }
            }
            finally
            {
                probe.Close();
                listedAfterClose = application.Windows.Cast<Window>().Contains(probe);
                if (!ReferenceEquals(application.MainWindow, previousMain)) { application.MainWindow = previousMain; }
                mainWindowAfter = application.MainWindow;
            }
        });

        List<HostObservation> records = [];
        foreach (Invocation invocation in invocations) { records.AddRange(HostObservationLog.TakeCurrentCase("D", invocation.IsMine)); }
        foreach (Invocation invocation in invocations) { await invocation.Result; }

        Assert.HasCount(Members.Length, records);
        CollectionAssert.AreEquivalent(Members, records.Select(record => Fields.Parse(record.Detail).Get("method")).ToArray());
        foreach (HostObservation record in records) { StringAssert.Contains(Fields.Parse(record.Detail).Get("owner"), nameof(DialogOwnerProbeWindow), record.Detail); }
        Assert.IsFalse(listedAfterClose, "未表示のWindowはCloseで Application.Windows から外れます。");
        Assert.IsNull(mainWindowAfter, "MainWindowは共有ホストの基準（null）に戻ります。");
        Assert.IsFalse(callbacks.Any);
    }

    /// <summary>
    /// 条件：<c>ShowMessageAsync</c> の題名と本文へ同じ入力（LF・CR・CRLF・連続LF・長さ200/201・CRLFをまたぐ切詰め・200文字目が高位サロゲートの入力）を渡し、nullは題名を省略したピッカーの要求で確かめます。
    /// 期待結果：改行はそれぞれ空白一つ（連続は畳まない）、200文字ちょうどはそのまま、201文字は先頭200文字と <c>…</c>、
    /// 置換が切詰めより先、サロゲートペアは分断せず199文字と <c>…</c>、nullは <c>-</c> です。題名と本文の両方に同じ規則が働きます。
    /// 検出する誤り：改行を置換しない・<c> / </c> にする、CRLFを二つに数える、連続を畳む、199/201文字で切る、<c>...</c> を付ける・付けない、
    /// 置換より前に切る、サロゲートペアを分断する、題名だけ・本文だけに適用する、nullを <c>-</c> にしない。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("LF")]
    [DataRow("CR")]
    [DataRow("CRLF")]
    [DataRow("LFLF")]
    [DataRow("Len200")]
    [DataRow("Len201")]
    [DataRow("CrlfCut")]
    [DataRow("Surrogate")]
    [DataRow("Null")]
    public async Task Summary_ReplacesLineBreaksAndTruncatesAt200Characters(string row)
    {
        string marker = "S-" + row + ":";
        (string input, string? expected) = row switch
        {
            "LF" => (marker + "a\nb", marker + "a b"),
            "CR" => (marker + "a\rb", marker + "a b"),
            "CRLF" => (marker + "a\r\nb", marker + "a b"),
            "LFLF" => (marker + "a\n\nb", marker + "a  b"),
            "Len200" => (Fill(marker, 200), Fill(marker, 200)),
            "Len201" => (Fill(marker, 201), Fill(marker, 200) + "…"),
            "CrlfCut" => (Fill(marker, 198) + "\r\nb", Fill(marker, 198) + " b"),
            "Surrogate" => (Fill(marker, 199) + "😀", Fill(marker, 199) + "…"),
            _ => (string.Empty, (string?)null),
        };
        var service = new UndeclaredUiDialogService();
        Func<HostObservation, bool> isMine = row == "Null"
            ? record => record.Detail.StartsWith("method=PickFileAsync | ", StringComparison.Ordinal) && record.Detail.Contains(" | title=- | text=- | ", StringComparison.Ordinal)
            : record => Fields.Parse(record.Detail).Get("title") == expected;

        Task task = row == "Null"
            ? service.PickFileAsync(new UiFilePickerRequest(title: null))
            : service.ShowMessageAsync(new UiMessageRequest(input, input, MessageBoxButton.OK, MessageBoxImage.Information));
        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("D", isMine);
        await task;

        Assert.HasCount(1, records);
        var fields = Fields.Parse(records[0].Detail);
        Assert.AreEqual(expected ?? "-", fields.Get("title"));
        Assert.AreEqual(expected ?? "-", fields.Get("text"));
    }

    /// <summary>
    /// 条件：記録用の窓口を渡さず <c>MainWindowViewModelTestFactory.Create()</c> で作った画面で、再検査の開始（確認）を要求します。
    /// 期待結果：構成の共通の窓口へ届いた確認が <c>D</c>（<c>method=ConfirmAsync</c>、題名は確認、本文は再検査の確認文の先頭行）として一件記録され、
    /// 開始結果はFailedで再検査は始まりません。
    /// 検出する誤り：Createが共通の窓口を渡さず構成の既定の窓口が使われる（記録がなく、OwnerUnavailableか実表示になる）、専用の枠にだけ渡す。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task FactoryCreate_WithoutDialogArguments_RecordsCommonConfirmationAsD()
    {
        MainWindowViewModel vm = MainWindowViewModelTestFactory.Create();
        using SettingsDialogViewModel settingsLifetime = vm.SettingDialog;
        string firstLine = Resources.Msg_rescan_all_charts_confirm.Split('\r', '\n')[0];
        string expectedPrefix = firstLine.Length > 200 ? firstLine[..200] : firstLine;

        MaintenanceRescanStartResult result = await vm.MaintenanceRescanWorkflow.RequestStartAsync();

        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("D", record =>
        {
            var fields = Fields.Parse(record.Detail);
            return fields.Get("method") == nameof(IUiDialogService.ConfirmAsync) && fields.Get("title") == Resources.Confirm;
        });
        Assert.HasCount(1, records);
        StringAssert.StartsWith(Fields.Parse(records[0].Detail).Get("text"), expectedPrefix);
        Assert.AreEqual(MaintenanceRescanStartStatus.Failed, result.Status);
        Assert.IsFalse(vm.MaintenanceRescanWorkflow.IsActive);
    }

    private static string Fill(string marker, int length) => marker + new string('x', length - marker.Length);

    private static void AssertKeyOrder(Fields fields, string member)
    {
        string[] keys = [.. fields.Keys];
        if (member == nameof(IUiDialogService.ShowWindowAsync))
        {
            Assert.HasCount(1, keys.Where(key => key == "result").ToArray(), "resultはちょうど一回現れます。");
            keys = [.. keys.Where(key => key != "result")];
        }
        CollectionAssert.AreEqual(new[] { "method", "request", "title", "text", "owner" }, keys);
    }

    private static Invocation Call(string member, UndeclaredUiDialogService service, Window? owner, string tag, CallbackFlags callbacks)
    {
        string title = tag + "-title\nl2";
        string text = tag + "-text\nl2";
        string expectedTitle = tag + "-title l2";
        string expectedText = tag + "-text l2";
        Func<HostObservation, bool> byTitle = record => Fields.Parse(record.Detail).Get("title") == expectedTitle;
        switch (member)
        {
            case nameof(IUiDialogService.ShowMessageAsync):
                return new(Map(service.ShowMessageAsync(new UiMessageRequest(text, title, UiDialogButton.OK, UiDialogIcon.Information, owner: owner)), r => (r.Status, r.Exception)),
                    byTitle, nameof(UiMessageRequest), expectedTitle, expectedText);
            case nameof(IUiDialogService.ConfirmAsync):
                return new(Map(service.ConfirmAsync(new UiConfirmationRequest(text, title, UiDialogButton.OKCancel, UiDialogIcon.Question, owner: owner)), r => (r.Status, r.Exception)),
                    byTitle, nameof(UiConfirmationRequest), expectedTitle, expectedText);
            case nameof(IUiDialogService.ShowWindowAsync):
                return new(Map(service.ShowWindowAsync(new UiWindowDialogRequest<Window, WindowResultProbe>(
                        () => { callbacks.WindowCreated = true; throw new InvalidOperationException("createWindow must not run."); },
                        _ => { callbacks.ResultCreated = true; throw new InvalidOperationException("createResult must not run."); },
                        owner)), r => (r.Status, r.Error)),
                    record => Fields.Parse(record.Detail).Get("method") == member && Fields.Parse(record.Detail).Get("result").Contains(nameof(WindowResultProbe), StringComparison.Ordinal),
                    nameof(UiWindowDialogRequest<Window, WindowResultProbe>), "-", "-");
            case nameof(IUiDialogService.PickFileAsync):
                return new(Map(service.PickFileAsync(new UiFilePickerRequest(title: title, owner: owner)), r => (r.Status, r.Error)),
                    byTitle, nameof(UiFilePickerRequest), expectedTitle, "-");
            case nameof(IUiDialogService.PickFolderAsync):
                return new(Map(service.PickFolderAsync(new UiFolderPickerRequest(title: title, owner: owner)), r => (r.Status, r.Error)),
                    byTitle, nameof(UiFolderPickerRequest), expectedTitle, "-");
            case nameof(IUiDialogService.PickSaveFileAsync):
                return new(Map(service.PickSaveFileAsync(new UiSaveFilePickerRequest(title: title, owner: owner)), r => (r.Status, r.Error)),
                    byTitle, nameof(UiSaveFilePickerRequest), expectedTitle, "-");
            case nameof(IUiDialogService.RunWithProgressAsync):
                return new(Map(service.RunWithProgressAsync(new UiProgressRequest(title, text, owner: owner),
                        _ => { callbacks.OperationRan = true; return Task.CompletedTask; }), r => (r.Status, r.Error)),
                    byTitle, nameof(UiProgressRequest), expectedTitle, expectedText);
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    private static async Task<(UiDialogStatus Status, Exception? Error)> Map<T>(Task<T> task, Func<T, (UiDialogStatus, Exception?)> select)
        => select(await task);

    private sealed record Invocation(
        Task<(UiDialogStatus Status, Exception? Error)> Result,
        Func<HostObservation, bool> IsMine,
        string RequestTypeSimpleName,
        string ExpectedTitle,
        string ExpectedText);

    private sealed class CallbackFlags
    {
        internal bool WindowCreated { get; set; }

        internal bool ResultCreated { get; set; }

        internal bool OperationRan { get; set; }

        internal bool Any => WindowCreated || ResultCreated || OperationRan;
    }

    /// <summary>記録の内容（<c>key=value</c> を <c> | </c> で並べた一行）を、鍵の出現順を保って読みます。値は最初の <c>=</c> より後です。</summary>
    private sealed class Fields
    {
        private readonly List<KeyValuePair<string, string>> pairs = [];

        internal IEnumerable<string> Keys => pairs.Select(pair => pair.Key);

        internal static Fields Parse(string detail)
        {
            var fields = new Fields();
            foreach (string part in detail.Split(" | "))
            {
                int separator = part.IndexOf('=');
                fields.pairs.Add(separator < 0 ? new(part, string.Empty) : new(part[..separator], part[(separator + 1)..]));
            }
            return fields;
        }

        internal string Get(string key) => pairs.FirstOrDefault(pair => pair.Key == key).Value ?? string.Empty;
    }

    private sealed class WindowResultProbe;

    private sealed class DialogOwnerProbeWindow : Window;
}
