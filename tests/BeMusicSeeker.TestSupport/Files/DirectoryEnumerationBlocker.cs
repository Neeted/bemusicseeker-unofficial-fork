using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.TestSupport.Files;

/// <summary>
/// 実在するディレクトリの列挙を、実OSの応答で失敗させたまま保持します。破棄すると解放し、削除保留の場合は実際に削除されます。
/// </summary>
/// <remarks>
/// 列挙の失敗を模擬の例外ではなくOSの実際の失敗にするための補助です。保持を確立した直後に、同じ列挙が期待した種類の例外になることを確かめ、
/// 再現できない環境では成功へ読み替えず失敗にします。
/// </remarks>
internal sealed class DirectoryEnumerationBlocker : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    private readonly SafeFileHandle handle;

    private DirectoryEnumerationBlocker(SafeFileHandle handle) => this.handle = handle;

    /// <summary>他の開き方をすべて拒否するハンドルを保持し、列挙を共有違反（<see cref="IOException"/>）にします。</summary>
    internal static DirectoryEnumerationBlocker HoldSharingViolation(string directory)
        => Hold<IOException>(directory, GenericRead, share: 0, deletePending: false);

    /// <summary>空のディレクトリを削除保留にし、列挙をアクセス拒否（<see cref="UnauthorizedAccessException"/>）にします。</summary>
    internal static DirectoryEnumerationBlocker HoldDeletePending(string directory)
        => Hold<UnauthorizedAccessException>(directory, GenericRead | Delete, ShareAll, deletePending: true);

    /// <summary>ハンドルを閉じます。削除保留のディレクトリはここで実際に消えます。</summary>
    public void Dispose() => handle.Dispose();

    private static DirectoryEnumerationBlocker Hold<TExpected>(string directory, uint access, uint share, bool deletePending)
        where TExpected : Exception
    {
        SafeFileHandle handle = CreateFile(directory, access, share, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "列挙を妨げるディレクトリのハンドルを開けません: " + directory);
        }
        try
        {
            if (deletePending)
            {
                int disposition = 1;
                if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, sizeof(int)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ディレクトリを削除保留にできません: " + directory);
                }
            }
            AssertEnumerationFails<TExpected>(directory);
            return new DirectoryEnumerationBlocker(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void AssertEnumerationFails<TExpected>(string directory) where TExpected : Exception
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (TExpected)
        {
            return;
        }
        catch (Exception failure)
        {
            throw new AssertFailedException(
                "列挙の失敗が期待した種類ではありません。期待: " + typeof(TExpected).Name + ", 実際: " + failure.GetType().Name, failure);
        }
        throw new AssertFailedException("ディレクトリの列挙を失敗させる状態を再現できません: " + directory);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref int information, int bufferSize);
}
