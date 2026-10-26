using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>ケース所有の実ディレクトリだけにドライブ別名を割り当て、利用側の終端後に自分の定義だけを解除します。</summary>
internal sealed class OwnedTestDrive(string device, string target, Semaphore reservation) : IDisposable
{
    /// <summary>確保したドライブの絶対ルートです。</summary>
    internal string Root => device + @"\";

    /// <summary>別ホストの同じ補助ともドライブ文字を分離して、一時実領域への別名を確保します。</summary>
    /// <param name="root">ケースが所有する一時実領域です。</param>
    /// <param name="excludedDriveLetters">予約・占有照会・定義を行わないドライブ文字です。省略時は従来の全候補を使います。</param>
    internal static OwnedTestDrive Create(string root, params char[] excludedDriveLetters)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "ManagedVolume")).FullName;
        string target = @"\??\" + directory;
        foreach (char letter in "ZYXWVUTSRQPONMLKJIHGFED")
        {
            if (excludedDriveLetters.Any(excluded => char.ToUpperInvariant(excluded) == letter)) { continue; }
            // Semaphore は取得スレッド以外でも解放でき、別ホストの確認→定義の隙間も予約します。
            var reservation = new Semaphore(1, 1, @"Local\BeMusicSeeker.Tests.OwnedTestDrive." + letter);
            if (!reservation.WaitOne(0)) { reservation.Dispose(); continue; }
            string device = letter + ":";
            bool transferred = false;
            try
            {
                if (ReadTargets(device).Length != 0 || Environment.GetLogicalDrives().Contains(device + @"\", StringComparer.OrdinalIgnoreCase)) { continue; }
                if (!DefineDosDevice(0x1 | 0x8, device, target)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                var owned = new OwnedTestDrive(device, target, reservation);
                transferred = true;
                try { owned.AssertOwned(); return owned; }
                catch (Exception failure)
                {
                    try { owned.Dispose(); }
                    catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
                    throw;
                }
            }
            finally
            {
                if (!transferred) { reservation.Release(); reservation.Dispose(); }
            }
        }
        throw new AssertFailedException("専用ドライブ別名を取得できません。");
    }

    /// <summary>書込みを始める前に唯一の定義がケース所有の実パスであることを確認します。</summary>
    internal void AssertOwned() => CollectionAssert.AreEqual(new[] { target }, ReadTargets(device));

    /// <summary>自分の固有targetだけを解除し、ホスト間の予約を返します。</summary>
    public void Dispose()
    {
        try
        {
            // EXACT_MATCH_ON_REMOVE により、別の定義が追加されても他者のtargetは解除しません。
            if (!DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, device, target)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            CollectionAssert.DoesNotContain(ReadTargets(device), target);
        }
        finally { reservation.Release(); reservation.Dispose(); }
    }

    private static string[] ReadTargets(string device)
    {
        char[] buffer = new char[32768];
        uint length = QueryDosDevice(device, buffer, buffer.Length);
        if (length != 0) { return new string(buffer, 0, checked((int)length)).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
        int error = Marshal.GetLastWin32Error();
        if (error == 2) { return []; }
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string device, string target);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, [Out] char[] target, int maximumLength);
}
