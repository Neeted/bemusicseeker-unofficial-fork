using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>名前変更で公開された完成ファイルの読取りを、名前変更に使ったハンドルが残る区間で検査します。</summary>
/// <remarks>
/// 区間は、完成名に対する <c>DELETE</c> 権限のハンドルを同じプロセスで保持して作ります。Windowsの共有検査は開いているハンドルの
/// アクセス権と新しく開く側の共有条件を照合するため、名前変更に使ったハンドルが残る区間と同じ条件になります。
/// 実際の名前変更の経路は Windows の <c>UpdateMarkerReaderSharingTests</c> が担当します。
/// </remarks>
[TestClass]
public sealed class PublishedFileTests
{
    /// <summary>
    /// 保証：<c>DELETE</c> 権限のハンドルが残る間も読取り専用で開け、開いている間は書込みを共有しません。
    /// 同じ区間で <see cref="File.ReadAllText(string)"/> が共有違反になる対照で前提を確かめ、削除の共有を落とす退行と、書込みの共有を許す退行を検出します。
    /// </summary>
    [TestMethod]
    public void OpenReadSharesRenameDeleteHandleWithoutSharingWrite()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("ready.json");
        File.WriteAllText(path, "published");

        SafeFileHandle holder = OpenDeleteHandle(path);
        FileStream stream;
        try
        {
            AssertSharingViolation(() => File.ReadAllText(path));
            stream = PublishedFile.OpenRead(path);
        }
        finally { holder.Dispose(); }

        using (stream)
        {
            Assert.IsTrue(stream.CanRead);
            Assert.IsFalse(stream.CanWrite);
            // 保持ハンドルの共有条件と区別するため、書込みの拒否は保持ハンドルを閉じた後に確かめます。
            AssertSharingViolation(() =>
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            });
        }
    }

    /// <summary>
    /// 保証：公開直後の区間でも一回の呼出しで内容の全体を返し、BOMの検出と既定のUTF-8は <see cref="File.ReadAllText(string)"/> と同じで、返った後に読取りハンドルを残しません。
    /// 同じ区間の対照で前提を確かめ、共有違反・途中までの読取り・文字コードの判定の誤り・ハンドルの回収漏れを検出します。
    /// </summary>
    /// <param name="encodingName">公開側が書く文字コードです。<c>utf-8</c> はBOMなし、<c>utf-16</c> はBOM付きのリトルエンディアンです。</param>
    [TestMethod]
    [DataRow("utf-8")]
    [DataRow("utf-16")]
    public void ReadAllTextReturnsWholeContentWhileRenameDeleteHandleIsOpen(string encodingName)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("ready.json");
        // 一回のバッファ（4096バイト）を超え、改行と日本語を含む内容にします。
        string expected = string.Concat(Enumerable.Range(0, 400).Select(index => $"行{index}：公開済みの内容\r\n"));
        byte[] bytes = encodingName == "utf-8"
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(expected)
            : [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(expected)];
        Assert.IsTrue(bytes.Length > 4096);
        File.WriteAllBytes(path, bytes);

        using (SafeFileHandle holder = OpenDeleteHandle(path))
        {
            AssertSharingViolation(() => File.ReadAllText(path));
            Assert.AreEqual(expected, PublishedFile.ReadAllText(path));
        }

        // 読取りハンドルが残っていれば、書込みを共有しないため書込みで開けません。排他で開く確認は、
        // すべての共有を許す外部のハンドル（ウイルス対策の検査など）でも失敗するため使いません。
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.AreEqual(bytes.LongLength, writer.Length);
    }

    private static void AssertSharingViolation(Action open)
    {
        IOException exception = Assert.ThrowsExactly<IOException>(open);
        Assert.AreEqual(32, exception.HResult & 0xffff, "ERROR_SHARING_VIOLATIONになります。");
    }

    /// <summary>名前変更が使うのと同じ <c>DELETE</c> 権限と共有条件で、完成名のハンドルを開いて返します。</summary>
    private static SafeFileHandle OpenDeleteHandle(string path)
    {
        const uint Delete = 0x00010000;
        const uint OpenExisting = 3;
        SafeFileHandle handle = CreateFileW(path, Delete, FileShare.Read | FileShare.Delete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
