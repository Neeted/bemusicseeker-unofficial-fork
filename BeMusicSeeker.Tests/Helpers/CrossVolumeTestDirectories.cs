using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>明示された別volumeの既存fixture基点に、ケース所有の子ディレクトリだけを確保します。</summary>
internal sealed class CrossVolumeTestDirectories : IDisposable
{
    private CrossVolumeTestDirectories(string sourceBaseDirectory, string destinationBaseDirectory)
    {
        SourceBaseDirectory = sourceBaseDirectory;
        DestinationBaseDirectory = destinationBaseDirectory;
    }

    /// <summary>ケースが作成し、回収する移動元の基点です。</summary>
    public string SourceBaseDirectory { get; }

    /// <summary>ケースが作成し、回収する移動先の基点です。</summary>
    public string DestinationBaseDirectory { get; }

    /// <summary>環境変数で指定された二つの実volumeを確認し、書込み探索なしで所有領域を作成します。</summary>
    /// <param name="testName">所有する子ディレクトリ名へ含めるテスト名。</param>
    /// <returns>生成した子ディレクトリのみを解放する所有者。</returns>
    public static CrossVolumeTestDirectories Create(string testName)
    {
        string sourceFixture = ReadFixtureBase("BMS_TEST_CROSS_VOLUME_SOURCE_ROOT");
        string destinationFixture = ReadFixtureBase("BMS_TEST_CROSS_VOLUME_DESTINATION_ROOT");
        Assert.AreNotEqual(Path.GetPathRoot(sourceFixture), Path.GetPathRoot(destinationFixture),
            ignoreCase: true, "クロスボリューム試験には異なるdrive rootが必要です。");
        // ドライブ別名やjunctionだけの差を別volumeと誤認しないよう、OSのmount pointからGUIDを照会します。
        Assert.AreNotEqual(ReadVolumeIdentity(sourceFixture), ReadVolumeIdentity(destinationFixture),
            ignoreCase: true, "指定したfixture基点は同じ実volumeです。");

        string suffix = SanitizePathPart(testName) + "_" + Guid.NewGuid().ToString("N");
        var directories = new CrossVolumeTestDirectories(
            Path.Combine(sourceFixture, "BeMusicSeeker_CrossVolume_Source_" + suffix),
            Path.Combine(destinationFixture, "BeMusicSeeker_CrossVolume_Destination_" + suffix));
        try
        {
            LongPathFileSystem.CreateDirectory(directories.SourceBaseDirectory);
            LongPathFileSystem.CreateDirectory(directories.DestinationBaseDirectory);
            return directories;
        }
        catch (Exception failure)
        {
            try { directories.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    /// <summary>指定されたfixture基点は保持し、このケースが生成した子だけを回収します。</summary>
    public void Dispose()
    {
        try { DeleteDirectoryIfExists(SourceBaseDirectory); }
        finally { DeleteDirectoryIfExists(DestinationBaseDirectory); }
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

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "test";
        }

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }
        return value;
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!LongPathFileSystem.DirectoryExists(directoryPath))
        {
            return;
        }

        foreach (string childFilePath in LongPathFileSystem.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childFilePath, FileAttributes.Normal);
        }

        foreach (string childDirectoryPath in LongPathFileSystem.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }

        LongPathFileSystem.SetAttributes(directoryPath, FileAttributes.Normal);
        LongPathFileSystem.DeleteDirectory(directoryPath, recursive: true);
    }
}
