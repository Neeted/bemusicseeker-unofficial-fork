using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BeMusicSeeker.Models.Utils;

internal static class LongPathFileSystem
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";
    private const string UncPathPrefix = @"\\";

    public readonly struct FileMetadata
    {
        public FileMetadata(long length, DateTime lastWriteTimeUtc)
        {
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public long Length { get; }

        public DateTime LastWriteTimeUtc { get; }
    }

    /// <summary>属性・列挙の失敗が正常な不存在を示す場合だけ真です。アクセス拒否や一般I/Oを不存在へ読み替えません。</summary>
    internal static bool IsNotFoundFailure(Exception failure)
        => failure is FileNotFoundException or DirectoryNotFoundException;

    /// <summary>登録位置の各要素を親の直下列挙で確認し、実在する綴りを返します。drive・共有名とリンク先は置換しません。</summary>
    /// <remarks>追加の表記確認だけが不能な場合は元入力を保持します。通常の利用可否検査は呼出元のpreflightが担当します。</remarks>
    internal static string ConfirmDirectoryPathSpelling(string path)
    {
        try
        {
            string normalized = TrimTrailingDirectorySeparators(NormalizePathForStorage(path));
            string root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root)) { return path; }
            string current = root;
            foreach (string component in normalized[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = EnumerateDirectories(current, component, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(candidate => string.Equals(Path.GetFileName(TrimTrailingDirectorySeparators(candidate)), component, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { return path; }
                current = Path.Combine(current, Path.GetFileName(TrimTrailingDirectorySeparators(entry)));
            }
            if ((GetAttributes(current) & FileAttributes.Directory) == 0) { return path; }
            return TrimTrailingDirectorySeparators(current);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return path;
        }
    }

    public static string NormalizePathForStorage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string normalized = RemoveExtendedPathPrefix(path);
        return IsFullyQualifiedWindowsPath(normalized)
            ? NormalizeFullyQualifiedWindowsPath(normalized)
            : Path.GetFullPath(normalized);
    }

    /// <summary>既存の物理単位照合規則でdot・末尾区切りを正規化します。復元では正規化の実失敗を原因として伝え、DB生キー自体は変更しません。</summary>
    internal static string NormalizePhysicalScopePath(string path)
        => TrimTrailingDirectorySeparators(NormalizePathForStorage(path?.Trim()));

    /// <summary>物理単位の照合用pathを作れた場合だけ真です。通常入力の既存の受理規則を維持し、FSを参照しません。</summary>
    /// <param name="path">既存の保留範囲または照合対象のパスです。</param>
    /// <param name="normalizedPath">従来の保留復元と同じ規則で得た照合用パスです。</param>
    /// <returns>正規化できた場合は真です。不能なDBキーのexact処理を拒否するものではありません。</returns>
    internal static bool TryNormalizePhysicalScopePath(string path, out string normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try
        {
            normalizedPath = NormalizePhysicalScopePath(path);
            return !string.IsNullOrWhiteSpace(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            normalizedPath = null;
            return false;
        }
    }

    public static string TrimTrailingDirectorySeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string root = Path.GetPathRoot(path);
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    public static bool IsSameOrDescendantDirectoryPath(string candidatePath, string ancestorPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(ancestorPath))
        {
            return false;
        }

        string normalizedCandidate = TrimTrailingDirectorySeparators(NormalizePathForStorage(candidatePath));
        string normalizedAncestor = TrimTrailingDirectorySeparators(NormalizePathForStorage(ancestorPath));
        return IsSameOrDescendantNormalizedDirectoryPath(normalizedCandidate, normalizedAncestor);
    }

    public static bool IsSameOrDescendantNormalizedDirectoryPath(string normalizedCandidate, string normalizedAncestor)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedAncestor))
        {
            return false;
        }
        if (string.Equals(normalizedCandidate, normalizedAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.Length > normalizedAncestor.Length
            && normalizedCandidate.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            && (EndsWithDirectorySeparator(normalizedAncestor)
                || IsDirectorySeparator(normalizedCandidate[normalizedAncestor.Length]));
    }

    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string fullPath = RemoveExtendedPathPrefix(path);
        fullPath = IsFullyQualifiedWindowsPath(fullPath)
            ? NormalizeFullyQualifiedWindowsPath(fullPath)
            : Path.GetFullPath(fullPath);
        if (IsExtendedPath(fullPath) || IsDevicePath(fullPath))
        {
            return fullPath;
        }
        if (fullPath.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return ExtendedUncPathPrefix + fullPath.Substring(UncPathPrefix.Length);
        }
        return ExtendedPathPrefix + fullPath;
    }

    public static byte[] ReadAllBytes(string path)
    {
        using FileStream stream = OpenRead(path);
        long length = stream.Length;
        if (length > int.MaxValue)
        {
            throw new IOException("File is too large to read into memory.");
        }

        byte[] bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of file.");
            }
            offset += read;
        }
        return bytes;
    }

    public static FileStream OpenRead(string path)
    {
        return new FileStream(ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        return new FileStream(ToExtendedPath(path), mode, access, share);
    }

    public static IEnumerable<string> ReadLines(string path, Encoding encoding)
    {
        using var reader = new StreamReader(OpenRead(path), encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            return File.Exists(ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return false;
        }
    }

    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            return Directory.Exists(ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return false;
        }
    }

    public static bool EntryExists(string path)
    {
        return FileExists(path) || DirectoryExists(path);
    }

    public static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(ToExtendedPath(path));
    }

    public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        if (!IsSameVolumeRoot(sourcePath, destinationPath))
        {
            MoveFileAcrossVolumeRoots(sourcePath, destinationPath, overwrite);
            return;
        }

        if (overwrite && FileExists(destinationPath))
        {
            ReplaceFileByMovingSource(sourcePath, destinationPath);
            return;
        }
        File.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
    }

    /// <summary>
    /// 同一 filesystem 上の staging file を、既存宛先の置換または初回移動で公開します。
    /// 宛先を先に削除しないため、公開に失敗しても既存ファイルを保持できます。
    /// </summary>
    /// <param name="sourcePath">閉じられた staging file のパスです。</param>
    /// <param name="destinationPath">公開先ファイルのパスです。</param>
    public static void PublishFile(string sourcePath, string destinationPath)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        if (FileExists(destinationPath))
        {
            File.Replace(
                ToExtendedPath(sourcePath),
                ToExtendedPath(destinationPath),
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
    }

    public static void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        File.Copy(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath), overwrite);
    }

    /// <summary>ディレクトリを再帰コピーします。指定時は各ディレクトリの内容列挙・宛先作成前に再解析ポイントを拒否します。</summary>
    /// <param name="sourcePath">コピー元ディレクトリ。</param>
    /// <param name="destinationPath">コピー先ディレクトリ。</param>
    /// <param name="overwrite">既存のコピー先への上書きを許可するか。</param>
    /// <param name="rejectDirectoryReparsePoints">ディレクトリへのリンクを辿らず、コピー全体を失敗させるか。既定では従来どおり辿ります。</param>
    public static void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, bool rejectDirectoryReparsePoints = false)
    {
        ThrowIfSamePath(sourcePath, destinationPath);
        ThrowIfDestinationIsInsideSourceDirectory(sourcePath, destinationPath);
        if (DirectoryExists(destinationPath) && !overwrite)
        {
            throw new IOException("Destination directory already exists.");
        }

        if (rejectDirectoryReparsePoints && (GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(string.Format(Properties.Resources.Error_DirectoryLinkCopyNotAllowedFormat, sourcePath));
        }
        CreateDirectory(destinationPath);
        foreach (string sourceDirectoryPath in GetDirectories(sourcePath))
        {
            string destinationChildPath = Path.Combine(destinationPath, Path.GetFileName(sourceDirectoryPath));
            CopyDirectory(sourceDirectoryPath, destinationChildPath, overwrite, rejectDirectoryReparsePoints);
        }
        foreach (string sourceFilePath in GetFiles(sourcePath))
        {
            string destinationFilePath = Path.Combine(destinationPath, Path.GetFileName(sourceFilePath));
            CopyFile(sourceFilePath, destinationFilePath, overwrite);
        }
    }

    /// <summary>
    /// 指定パスと同じ filesystem の sibling path を一意に生成します。
    /// 生成だけを行い、filesystem へは何も書き込みません。
    /// </summary>
    /// <param name="path">対象 destination path です。</param>
    /// <param name="purpose">一時 path の識別子です。</param>
    /// <returns>対象 path と同じ親を持つ未使用の path です。</returns>
    public static string CreateMutationSiblingPath(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string directoryPath = Path.GetDirectoryName(path) ?? string.Empty;
        string normalizedPurpose = string.IsNullOrWhiteSpace(purpose)
            ? "mutation"
            : new string(purpose.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedPurpose))
        {
            normalizedPurpose = "mutation";
        }

        string candidatePath;
        do
        {
            candidatePath = Path.Combine(
                directoryPath,
                ".bemusicseeker-" + normalizedPurpose + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        }
        while (EntryExists(candidatePath));
        return candidatePath;
    }

    /// <summary>
    /// ディレクトリ移動先の衝突を判定します。宛先が存在しなければ衝突なしです。
    /// 元と宛先が大小文字だけ異なる場合は、両パスを開いたボリュームとファイルIDで同一の物理ディレクトリかをO(1)で比較し、同一なら衝突なし(外部で実表記が変わった場合を含む)、別実体(大小文字を区別するディレクトリ内の別エントリ)なら衝突とします。
    /// 識別子は128bitのFileIdInfoを優先し、FileIdInfo非対応のFS(FAT・exFAT・一部のネットワーク共有)では両パスとも同じ方式でGetFileInformationByHandleの64bit索引へ代替して比較します。
    /// 親ディレクトリは列挙しません。両方式でハンドル取得や識別子取得に失敗した場合は例外を漏らさず、従来どおり宛先が存在するものとして衝突ありを返します。
    /// </summary>
    internal static bool HasDirectoryMoveDestinationConflict(string sourcePath, string destinationPath)
    {
        if (!EntryExists(destinationPath)) { return false; }
        if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)) { return true; }
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)) { return true; }
        if (!OperatingSystem.IsWindows()) { return true; }
        return !TryIsSamePhysicalDirectory(sourcePath, destinationPath, out bool same) || !same;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryForIdentity(
        string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    /// <summary>ネイティブの BY_HANDLE_FILE_INFORMATION(FILETIME は4バイト境界の52バイト)と同じ配置です。Pack=4 を外すと ulong が8バイト境界へずれ、識別子が誤った位置から読まれます。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFileIdInfo(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, out FileIdInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadByHandleFileInformation(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>
    /// ディレクトリのボリュームとファイルIDを読みます。既定は128bitのFileIdInfo(NTFS/ReFS)で、legacyではFileIdInfo非対応のFS(FAT・exFAT・一部のネットワーク共有)向けにBY_HANDLE_FILE_INFORMATIONの64bit索引を使います。
    /// 異なる方式の識別子は比較できないため、呼出し側が両パスを同じ方式で読みます。
    /// </summary>
    internal static bool TryReadDirectoryIdentity(string path, bool legacy, out (ulong Volume, ulong IdLow, ulong IdHigh) identity)
    {
        identity = default;
        try
        {
            // 属性読取りだけの最小アクセスで、共有を全許可し BACKUP_SEMANTICS でディレクトリを開きます。
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = OpenDirectoryForIdentity(
                ToExtendedPath(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) { return false; }
            if (legacy)
            {
                if (!ReadByHandleFileInformation(handle, out ByHandleFileInformation legacyInfo)) { return false; }
                identity = (legacyInfo.VolumeSerialNumber, ((ulong)legacyInfo.FileIndexHigh << 32) | legacyInfo.FileIndexLow, 0);
                return true;
            }
            if (!ReadFileIdInfo(handle, 18, out FileIdInfo info, (uint)Marshal.SizeOf<FileIdInfo>())) { return false; }
            identity = (info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryIsSamePhysicalDirectory(string firstPath, string secondPath, out bool same)
    {
        same = false;
        // どちらか一方でもFileIdInfoを読めなければ、両方を同じ代替方式で読み直します。
        foreach (bool legacy in new[] { false, true })
        {
            if (TryReadDirectoryIdentity(firstPath, legacy, out (ulong Volume, ulong IdLow, ulong IdHigh) first) && TryReadDirectoryIdentity(secondPath, legacy, out (ulong Volume, ulong IdLow, ulong IdHigh) second))
            {
                same = first == second;
                return true;
            }
        }
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveDirectorySpelling(string sourcePath, string destinationPath, uint flags);

    /// <summary>ディレクトリを移動します。大小文字だけの変更も単一の物理改名で完了し、中間配置を作りません。</summary>
    public static void MoveDirectory(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)
            && string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(ToExtendedPath(sourcePath)) & FileAttributes.Directory) == 0)
            { throw new IOException("Source is not a directory."); }
            if (HasDirectoryMoveDestinationConflict(sourcePath, destinationPath))
            { throw new IOException("Destination directory already exists."); }
            if (OperatingSystem.IsWindows())
            {
                // Directory.Move の同一パス拒否を避け、置換フラグなしで最終表記へ直接変更します。
                if (!MoveDirectorySpelling(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath), 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(new Win32Exception(error).Message, unchecked((int)0x80070000) | error);
                }
            }
            else { Directory.Move(sourcePath, destinationPath); }
            return;
        }
        ThrowIfSamePath(sourcePath, destinationPath);
        ThrowIfDestinationIsInsideSourceDirectory(sourcePath, destinationPath);
        bool destinationExists = DirectoryExists(destinationPath);
        if (!overwrite && destinationExists)
        {
            throw new IOException("Destination directory already exists.");
        }

        if (!destinationExists)
        {
            if (IsSameVolumeRoot(sourcePath, destinationPath))
            {
                Directory.Move(ToExtendedPath(sourcePath), ToExtendedPath(destinationPath));
            }
            else
            {
                MoveDirectoryAcrossVolumeRoots(sourcePath, destinationPath, overwrite);
            }
            return;
        }

        MergeDirectory(sourcePath, destinationPath);
        DeleteDirectory(sourcePath, recursive: false);
    }

    public static void DeleteFile(string path)
    {
        File.Delete(ToExtendedPath(path));
    }

    public static void DeleteDirectory(string path, bool recursive)
    {
        Directory.Delete(ToExtendedPath(path), recursive);
    }

    public static string[] GetFiles(string path)
    {
        return Directory.GetFiles(ToExtendedPath(path))
            .Select(NormalizePathForStorage)
            .ToArray();
    }

    public static string[] GetDirectories(string path)
    {
        return Directory.GetDirectories(ToExtendedPath(path))
            .Select(NormalizePathForStorage)
            .ToArray();
    }

    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string filePath in Directory.EnumerateFiles(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(filePath);
        }
    }

    public static IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string directoryPath in Directory.EnumerateDirectories(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(directoryPath);
        }
    }

    public static IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(ToExtendedPath(path), searchPattern, searchOption))
        {
            yield return NormalizePathForStorage(entryPath);
        }
    }

    public static FileAttributes GetAttributes(string path)
    {
        return File.GetAttributes(ToExtendedPath(path));
    }

    public static void SetAttributes(string path, FileAttributes attributes)
    {
        File.SetAttributes(ToExtendedPath(path), attributes);
    }

    public static void SetCreationTime(string path, bool isDirectory, DateTime creationTime)
    {
        if (isDirectory)
        {
            Directory.SetCreationTime(ToExtendedPath(path), creationTime);
        }
        else
        {
            File.SetCreationTime(ToExtendedPath(path), creationTime);
        }
    }

    public static void SetLastWriteTime(string path, bool isDirectory, DateTime lastWriteTime)
    {
        if (isDirectory)
        {
            Directory.SetLastWriteTime(ToExtendedPath(path), lastWriteTime);
        }
        else
        {
            File.SetLastWriteTime(ToExtendedPath(path), lastWriteTime);
        }
    }

    public static void SetLastAccessTime(string path, bool isDirectory, DateTime lastAccessTime)
    {
        if (isDirectory)
        {
            Directory.SetLastAccessTime(ToExtendedPath(path), lastAccessTime);
        }
        else
        {
            File.SetLastAccessTime(ToExtendedPath(path), lastAccessTime);
        }
    }

    public static DateTime GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(ToExtendedPath(path));
    }

    public static DateTime GetLastWriteTimeUtc(string path, bool isDirectory)
    {
        return isDirectory
            ? Directory.GetLastWriteTimeUtc(ToExtendedPath(path))
            : File.GetLastWriteTimeUtc(ToExtendedPath(path));
    }

    public static DateTime GetLastWriteTime(string path, bool isDirectory)
    {
        return isDirectory
            ? Directory.GetLastWriteTime(ToExtendedPath(path))
            : File.GetLastWriteTime(ToExtendedPath(path));
    }

    public static long GetFileLength(string path)
    {
        return new FileInfo(ToExtendedPath(path)).Length;
    }

    public static FileMetadata GetFileMetadata(string path)
    {
        var fileInfo = new FileInfo(ToExtendedPath(path));
        return new FileMetadata(fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    private static void ReplaceFileByMovingSource(string sourcePath, string destinationPath)
    {
        string replacementPath = CreateMutationSiblingPath(destinationPath, "replace");
        bool sourceMovedToReplacement = false;
        try
        {
            File.Move(ToExtendedPath(sourcePath), ToExtendedPath(replacementPath));
            sourceMovedToReplacement = true;
            try
            {
                File.Replace(ToExtendedPath(replacementPath), ToExtendedPath(destinationPath), null, ignoreMetadataErrors: true);
            }
            catch (Exception replaceException)
            {
                Exception restoreException = TryRestoreSourceAfterReplaceFailure(replacementPath, sourcePath);
                if (restoreException != null)
                {
                    throw new IOException("Failed to replace destination file and failed to restore source file.", new AggregateException(replaceException, restoreException));
                }
                throw;
            }
        }
        catch
        {
            if (!sourceMovedToReplacement)
            {
                TryDeleteTemporaryReplacement(replacementPath);
            }
            throw;
        }
    }

    private static void MoveFileAcrossVolumeRoots(string sourcePath, string destinationPath, bool overwrite)
    {
        if (overwrite && FileExists(destinationPath))
        {
            CopyAndReplaceFileAcrossVolumeRoots(sourcePath, destinationPath);
            DeleteFile(sourcePath);
            return;
        }

        CopyFile(sourcePath, destinationPath, overwrite);
        DeleteFile(sourcePath);
    }

    private static void CopyAndReplaceFileAcrossVolumeRoots(string sourcePath, string destinationPath)
    {
        string replacementPath = CreateMutationSiblingPath(destinationPath, "replace");
        try
        {
            CopyFile(sourcePath, replacementPath, overwrite: false);
            File.Replace(ToExtendedPath(replacementPath), ToExtendedPath(destinationPath), null, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDeleteTemporaryReplacement(replacementPath);
            throw;
        }
    }

    private static void MoveDirectoryAcrossVolumeRoots(string sourcePath, string destinationPath, bool overwrite)
    {
        CopyDirectory(sourcePath, destinationPath, overwrite);
        DeleteDirectory(sourcePath, recursive: true);
    }

    private static Exception TryRestoreSourceAfterReplaceFailure(string replacementPath, string sourcePath)
    {
        try
        {
            if (!FileExists(sourcePath) && FileExists(replacementPath))
            {
                File.Move(ToExtendedPath(replacementPath), ToExtendedPath(sourcePath));
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void TryDeleteTemporaryReplacement(string replacementPath)
    {
        try
        {
            if (FileExists(replacementPath))
            {
                DeleteFile(replacementPath);
            }
        }
        catch
        {
        }
    }

    private static void MergeDirectory(string sourcePath, string destinationPath)
    {
        CreateDirectory(destinationPath);
        foreach (string sourceDirectoryPath in GetDirectories(sourcePath))
        {
            string destinationChildPath = Path.Combine(destinationPath, Path.GetFileName(sourceDirectoryPath));
            if (DirectoryExists(destinationChildPath))
            {
                MergeDirectory(sourceDirectoryPath, destinationChildPath);
                DeleteDirectory(sourceDirectoryPath, recursive: false);
            }
            else
            {
                MoveDirectory(sourceDirectoryPath, destinationChildPath, overwrite: true);
            }
        }

        foreach (string sourceFilePath in GetFiles(sourcePath))
        {
            string destinationFilePath = Path.Combine(destinationPath, Path.GetFileName(sourceFilePath));
            MoveFile(sourceFilePath, destinationFilePath, overwrite: true);
        }
    }

    private static void ThrowIfSamePath(string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizePathForStorage(sourcePath);
        string normalizedDestinationPath = NormalizePathForStorage(destinationPath);
        if (string.Equals(normalizedSourcePath, normalizedDestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Source path and destination path are the same.");
        }
    }

    private static bool IsSameVolumeRoot(string sourcePath, string destinationPath)
    {
        string sourceRoot = NormalizePathRootForComparison(sourcePath);
        string destinationRoot = NormalizePathRootForComparison(destinationPath);
        return string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathRootForComparison(string path)
    {
        string normalizedPath = NormalizePathForStorage(path);
        string root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return TrimTrailingDirectorySeparators(root);
    }

    private static void ThrowIfDestinationIsInsideSourceDirectory(string sourcePath, string destinationPath)
    {
        string normalizedSourcePath = NormalizeDirectoryPathForComparison(sourcePath);
        string normalizedDestinationPath = NormalizeDirectoryPathForComparison(destinationPath);
        if (!string.Equals(normalizedSourcePath, normalizedDestinationPath, StringComparison.OrdinalIgnoreCase)
            && IsSameOrDescendantNormalizedDirectoryPath(normalizedDestinationPath, normalizedSourcePath))
        {
            throw new IOException("Destination path is inside the source directory.");
        }
    }

    private static string NormalizeDirectoryPathForComparison(string path)
    {
        return TrimTrailingDirectorySeparators(NormalizePathForStorage(path));
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return !string.IsNullOrEmpty(path)
            && IsDirectorySeparator(path[path.Length - 1]);
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UncPathPrefix + path.Substring(ExtendedUncPathPrefix.Length);
        }
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = path.Substring(ExtendedPathPrefix.Length);
            return IsDriveRootedPath(suffix)
                ? suffix
                : path;
        }
        return path;
    }

    private static bool IsDriveRootedPath(string path)
    {
        return path?.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && IsDirectorySeparator(path[2]);
    }

    private static bool IsExtendedPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDevicePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    private static bool IsFullyQualifiedWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (IsExtendedPath(path) || IsDevicePath(path))
        {
            return true;
        }
        if (IsDriveRootedPath(path))
        {
            return true;
        }
        return path.StartsWith(UncPathPrefix, StringComparison.Ordinal);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    private static string NormalizeFullyQualifiedWindowsPath(string path)
    {
        if (!NeedsFullPathNormalization(path))
        {
            return path;
        }

        if (IsDevicePath(path))
        {
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string root = GetFullyQualifiedRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return normalized;
        }

        var parts = new List<string>();
        string remainder = normalized.Substring(root.Length);
        foreach (string part in remainder.Split(Path.DirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(part) || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(part, "..", StringComparison.Ordinal))
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(part);
        }

        return parts.Count == 0
            ? root
            : root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
    }

    private static bool NeedsFullPathNormalization(string path)
    {
        return path.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || path.IndexOf(@"\.", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\.", StringComparison.Ordinal)
            || path.IndexOf(@"\..", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\..", StringComparison.Ordinal);
    }

    private static string GetFullyQualifiedRoot(string path)
    {
        if (IsDriveRootedPath(path))
        {
            return path.Substring(0, 3);
        }
        if (!path.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int serverEnd = path.IndexOf(Path.DirectorySeparatorChar, UncPathPrefix.Length);
        if (serverEnd < 0)
        {
            return path;
        }
        int shareEnd = path.IndexOf(Path.DirectorySeparatorChar, serverEnd + 1);
        return shareEnd < 0
            ? path
            : path.Substring(0, shareEnd + 1);
    }
}
