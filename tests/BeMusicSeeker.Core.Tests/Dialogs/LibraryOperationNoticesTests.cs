using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryOperationNoticesTests
{
    private static BMSLibrary.OperationDialogMessage Notice(
        string text = "text",
        string caption = "caption",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Exclamation,
        MessageBoxResult defaultResult = MessageBoxResult.OK,
        int count = 1)
        => new(text, caption, button, icon, defaultResult, count);

    /// <summary>
    /// 本文・見出し・ボタン・アイコン・既定値が一致する案内は最初の一件だけを加えた順に残し、一項目でも違う案内と別のスレッドの追加を残して、取得後の追加で取得済みの写しを変えない。
    /// </summary>
    /// <remarks>
    /// 重複の判定を段階や内側・外側ごとに分けると、同じ案内が二重に表示される。スレッドごとの入れ物にすると、別のスレッドで加えた案内を取り逃がす。
    /// 取得時の写しを返さないと、表示の途中で追加された案内が表示済みの列へ混ざる。
    /// </remarks>
    [TestMethod]
    public async Task Add_KeepsFirstOfIdenticalNoticesInOrderIncludingOtherThreadsAndSnapshotIsIndependent()
    {
        var notices = new LibraryOperationNotices();
        BMSLibrary.OperationDialogMessage first = Notice();
        BMSLibrary.OperationDialogMessage otherCaption = Notice(caption: "other");
        BMSLibrary.OperationDialogMessage duplicate = Notice(count: 3);
        BMSLibrary.OperationDialogMessage[] variants =
        [
            Notice(text: "text2"),
            Notice(caption: "caption2"),
            Notice(button: MessageBoxButton.OKCancel),
            Notice(icon: MessageBoxImage.Hand),
            Notice(defaultResult: MessageBoxResult.None),
            Notice(text: "Text"),
        ];
        BMSLibrary.OperationDialogMessage fromOtherThread = Notice(text: "thread");

        notices.Add(first);
        notices.Add(otherCaption);
        notices.Add(duplicate);
        foreach (BMSLibrary.OperationDialogMessage variant in variants) { notices.Add(variant); }
        await Task.Run(() => notices.Add(fromOtherThread));
        notices.Add(Notice(text: "thread"));

        IReadOnlyList<BMSLibrary.OperationDialogMessage> snapshot = notices.Snapshot();
        BMSLibrary.OperationDialogMessage[] expected = [first, otherCaption, .. variants, fromOtherThread];
        Assert.AreEqual(expected.Length, snapshot.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreSame(expected[index], snapshot[index], "index=" + index);
        }

        notices.Add(Notice(text: "after snapshot"));
        Assert.AreEqual(expected.Length, snapshot.Count);
        Assert.AreEqual(expected.Length + 1, notices.Snapshot().Count);
    }

    /// <summary>
    /// null の案内は加えられず、空の収集先の取得は空の列を返す。
    /// </summary>
    /// <remarks>null を黙って受け付けると、案内の欠落が表示のない成功に見える。</remarks>
    [TestMethod]
    public void Add_RejectsNullAndEmptySnapshotIsEmpty()
    {
        var notices = new LibraryOperationNotices();

        Assert.AreEqual(0, notices.Snapshot().Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => notices.Add(null!));
        Assert.AreEqual(0, notices.Snapshot().Count);
    }
}
