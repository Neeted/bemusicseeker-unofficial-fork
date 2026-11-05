using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateMarkerReaderSharingTests
{
    /// <summary>
    /// 完成名への移動後もDELETEハンドルが残る間に、proceedとcancelを一回で判定・消費する。
    /// 同じハンドル保持中の旧共有読取りが共有違反になる対照で、共有条件の退行を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("proceed", true)]
    [DataRow("cancel", false)]
    public void DecisionReaderAcceptsRenamedMarkerBeforeRenameHandleCloses(string decision, bool expected)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string appDirectory = workspace.GetPath("app");
        string currentDirectory = Path.Combine(appDirectory, "update_work", "current");
        Directory.CreateDirectory(currentDirectory);
        string decisionPath = Path.Combine(currentDirectory, "updater-decision.txt");
        object request = ParseUpdateRequest(appDirectory, currentDirectory, decisionPath);

        using (SafeFileHandle renameHandle = RenameCompletedMarker(decisionPath, Encoding.UTF8.GetBytes(decision)))
        {
            AssertReadSharingFails(decisionPath);
            Type program = request.GetType().DeclaringType
                ?? throw new InvalidOperationException("更新要求の宣言型がありません。");
            MethodInfo wait = program.GetMethod("WaitForLaunchDecision", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(program.FullName, "WaitForLaunchDecision");

            Assert.AreEqual(expected, wait.Invoke(null, [request]));
        }

        Assert.IsFalse(File.Exists(decisionPath), "決定は読取り後に消費されます。");
    }

    /// <summary>
    /// 起動delegate内で完成名へ移動したreadyを、移動用DELETEハンドルの解放前に通常Gatewayが受理する。
    /// receiptと完全なbytesを観測し、共有違反による起動失敗と読取りハンドルの回収漏れを検出する。
    /// </summary>
    [TestMethod]
    public void ReadyReaderAcceptsRenamedMarkerBeforeRenameHandleCloses()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string readyPath = workspace.GetPath("updater-ready.txt");
        byte[] expectedBytes = Encoding.UTF8.GetBytes("1");
        using Process process = new();
        SafeFileHandle? renameHandle = null;
        Exception? renameFailure = null;
        try
        {
            var gateway = new WindowsUpdaterProcessGateway(_ =>
            {
                // 起動失敗に包まれないよう、delegate内では名前の変更とハンドル保持だけを行います。
                try
                {
                    renameHandle = RenameCompletedMarker(readyPath, expectedBytes);
                }
                catch (Exception ex)
                {
                    renameFailure = ex;
                }
                return process;
            });
            var request = UpdaterProcessLaunchRequest.Create(
                workspace.GetPath("BeMusicSeeker.Updater.exe"), root, root,
                workspace.GetPath("package.zip"), workspace.GetPath("update_backup"), readyPath,
                workspace.GetPath("updater-decision.txt"), workspace.GetPath("BeMusicSeeker.exe"));

            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

            Assert.IsNull(renameFailure, renameFailure?.ToString());
            Assert.IsNotNull(receipt);
            // ハンドルはfinallyまで保持されるため、旧共有が共有違反になる同じ区間をGateway受理後に確認できます。
            AssertReadSharingFails(readyPath);
        }
        finally
        {
            renameHandle?.Dispose();
        }

        using var exclusive = new FileStream(readyPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var contents = new MemoryStream();
        exclusive.CopyTo(contents);
        CollectionAssert.AreEqual(expectedBytes, contents.ToArray());
    }

    // 本番API追加や更新全体の準備を避け、実際の読取り・判定だけを検証する限定反射です。
    // 更新テストがParseとWaitForLaunchDecisionだけを管理し、通常の狭い境界が得られた時に退役します。
    private static object ParseUpdateRequest(string appDirectory, string currentDirectory, string decisionPath)
    {
        string assemblyPath = Path.Combine(AppContext.BaseDirectory,
            "fixtures", "windows-process-probe", "fixtures", "managed-updater", "BeMusicSeeker.Updater.dll");
        Type program = Assembly.LoadFrom(assemblyPath).GetType("BeMusicSeeker.Updater.Program", throwOnError: true)
            ?? throw new InvalidOperationException("更新ツールのProgram型がありません。");
        Type requestType = program.GetNestedType("UpdateRequest", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("更新要求の型がありません。");
        MethodInfo parse = requestType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(requestType.FullName, "Parse");
        string[] arguments =
        [
            "--app-dir", appDirectory,
            "--package", Path.Combine(currentDirectory, "package.zip"),
            "--backup-dir", Path.Combine(appDirectory, "update_backup"),
            "--ready-file", Path.Combine(currentDirectory, "updater-ready.txt"),
            "--decision-file", decisionPath,
            "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--restart-exe", Path.Combine(appDirectory, "BeMusicSeeker.exe")
        ];
        return parse.Invoke(null, [arguments])
            ?? throw new InvalidOperationException("更新要求を解析できませんでした。");
    }

    private static void AssertReadSharingFails(string markerPath)
    {
        IOException exception = Assert.ThrowsExactly<IOException>(() =>
        {
            using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        Assert.AreEqual(32, exception.HResult & 0xffff, "旧共有ではERROR_SHARING_VIOLATIONになります。");
    }

    // 完成内容を閉じてから、実renameに使った同じDELETEハンドルを呼出元へ渡します。
    // 同期呼出しとハンドル寿命で、名前の変更からcloseまでの区間を固定します。
    private static SafeFileHandle RenameCompletedMarker(string markerPath, byte[] contents)
    {
        string temporaryPath = markerPath + ".publishing";
        File.WriteAllBytes(temporaryPath, contents);
        SafeFileHandle handle = CreateFileW(temporaryPath, 0x00010000, FileShare.Read | FileShare.Delete,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(markerPath));
            int nameOffset = Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileName)).ToInt32();
            // APIの規約どおり構造体全体の大きさに名前の長さを足します。FileNameの先頭位置までだけだと
            // 名前末尾の後ろ（未初期化領域）が名前に混入し、完成名が一定せず移動後のファイルが見つかりません。
            int bufferSize = Marshal.SizeOf<FileRenameInfo>() + name.Length;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
                Marshal.StructureToPtr(new FileRenameInfo { FileNameLength = (uint)name.Length }, buffer, false);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                if (!SetFileInformationByHandle(handle, 3, buffer, (uint)bufferSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        IntPtr information, uint bufferSize);
}
