using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <summary>明示された別volumeの既存fixture基点に、ケース所有の子ディレクトリだけを確保します。</summary>
internal sealed class CrossVolumeTestDirectories
{
    private readonly TestWorkspace sourceWorkspace;
    private readonly TestWorkspace destinationWorkspace;

    private CrossVolumeTestDirectories(TestWorkspace sourceWorkspace, TestWorkspace destinationWorkspace)
    {
        this.sourceWorkspace = sourceWorkspace;
        this.destinationWorkspace = destinationWorkspace;
        SourceBaseDirectory = sourceWorkspace.RootDirectory;
        DestinationBaseDirectory = destinationWorkspace.RootDirectory;
    }

    /// <summary>ケースが作成し、回収する移動元の基点です。</summary>
    public string SourceBaseDirectory { get; }

    /// <summary>ケースが作成し、回収する移動先の基点です。</summary>
    public string DestinationBaseDirectory { get; }

    /// <summary>別volumeの専有媒体を一度所有し、本体と両媒体の回収失敗を別保持します。入力基点は削除しません。</summary>
    internal static void Run(Action<CrossVolumeTestDirectories> test)
    {
        CrossVolumeTestDirectories directories = Create();
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try { test(directories); }
        catch (Exception failure) { primary = failure; }
        finally
        {
            foreach (TestWorkspace workspace in new[] { directories.sourceWorkspace, directories.destinationWorkspace })
            {
                try { workspace.Dispose(); }
                catch (Exception failure)
                {
                    workspace.PreserveArtifacts();
                    failure.Data["PreservedCrossVolumeWorkspace"] = workspace.RootDirectory;
                    cleanup.Add(failure);
                }
            }
        }
        if (primary is not null)
        {
            if (cleanup.Count > 0) { primary.Data["CrossVolumeCleanupFailures"] = new AggregateException(cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count > 0) { throw new AggregateException("別volume媒体の回収に失敗しました。", cleanup); }
    }

    /// <summary>二つの実volumeを確認し、部分作成失敗時も元例外を保って作成済み媒体だけを回収します。</summary>
    private static CrossVolumeTestDirectories Create()
    {
        string sourceFixture = ReadFixtureBase("BMS_TEST_CROSS_VOLUME_SOURCE_ROOT");
        string destinationFixture = ReadFixtureBase("BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT");
        Assert.AreNotEqual(Path.GetPathRoot(sourceFixture), Path.GetPathRoot(destinationFixture),
            ignoreCase: true, "クロスボリューム試験には異なるdrive rootが必要です。");
        // ドライブ別名やjunctionだけの差を別volumeと誤認しないよう、OSのmount pointからGUIDを照会します。
        Assert.AreNotEqual(ReadVolumeIdentity(sourceFixture), ReadVolumeIdentity(destinationFixture),
            ignoreCase: true, "指定したfixture基点は同じ実volumeです。");

        var source = TestWorkspace.Create(sourceFixture);
        try
        {
            return new CrossVolumeTestDirectories(source, TestWorkspace.Create(destinationFixture));
        }
        catch (Exception failure)
        {
            try { source.Dispose(); }
            catch (Exception cleanupFailure)
            {
                source.PreserveArtifacts();
                cleanupFailure.Data["PreservedCrossVolumeWorkspace"] = source.RootDirectory;
                failure.Data["CrossVolumeCleanupFailures"] = new AggregateException(cleanupFailure);
            }
            throw;
        }
    }

    private static string ReadFixtureBase(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        { throw new AssertFailedException(variableName + "に既存fixture基点の絶対パスを指定してください。"); }
        Assert.IsTrue(Path.IsPathFullyQualified(value), variableName + "は絶対パスで指定してください。");
        string path = Path.GetFullPath(value);
        string? root = Path.GetPathRoot(path);
        Assert.IsTrue(root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':',
            variableName + "はローカルdrive root配下を指定してください。");
        Assert.IsTrue(Directory.Exists(path), variableName + "のfixture基点が存在しません。");
        return path;
    }

    private static string ReadVolumeIdentity(string path)
    {
        var mountPoint = new StringBuilder(32768);
        if (!GetVolumePathName(path, mountPoint, mountPoint.Capacity))
        { throw new Win32Exception(Marshal.GetLastWin32Error(), "fixture基点のmount pointを取得できません。"); }
        var volumeName = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), volumeName, volumeName.Capacity))
        { throw new Win32Exception(Marshal.GetLastWin32Error(), "fixture基点のvolume GUIDを取得できません。"); }
        return volumeName.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder mountPoint, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);

}
