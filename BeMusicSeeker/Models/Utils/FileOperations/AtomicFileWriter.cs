using System;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 同一ディレクトリの staging file を閉じてから、ファイルを置換または初回公開します。
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>
    /// staging file へ内容を書き込み、完了後に destination へ公開します。
    /// </summary>
    /// <param name="destinationPath">公開先ファイルのパスです。</param>
    /// <param name="writeStagingFile">開いた staging stream へ内容を書き込む処理です。</param>
    /// <exception cref="IOException">書込みまたは公開に失敗した場合に送出されます。</exception>
    internal static void Write(string destinationPath, Action<Stream> writeStagingFile)
    {
        Write(
            destinationPath,
            writeStagingFile,
            LongPathFileSystem.PublishFile,
            LongPathFileSystem.DeleteFile);
    }

    /// <summary>
    /// 呼出し単位の filesystem 委譲を使って、staging file を destination へ公開します。
    /// </summary>
    /// <param name="destinationPath">公開先ファイルのパスです。</param>
    /// <param name="writeStagingFile">開いた staging stream へ内容を書き込む処理です。</param>
    /// <param name="publishStagingFile">閉じた staging file を公開する処理です。</param>
    /// <param name="deleteStagingFile">所有する staging file を削除する処理です。</param>
    internal static void Write(
        string destinationPath,
        Action<Stream> writeStagingFile,
        Action<string, string> publishStagingFile,
        Action<string> deleteStagingFile)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentNullException(nameof(destinationPath));
        }
        ArgumentNullException.ThrowIfNull(writeStagingFile);
        ArgumentNullException.ThrowIfNull(publishStagingFile);
        ArgumentNullException.ThrowIfNull(deleteStagingFile);

        string normalizedDestinationPath = LongPathFileSystem.NormalizePathForStorage(destinationPath);
        string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(
            normalizedDestinationPath,
            "atomic-save");
        bool stagingFileCreated = false;
        bool published = false;
        Exception primaryFailure = null;
        Exception cleanupFailure = null;

        try
        {
            using (FileStream stagingStream = LongPathFileSystem.Open(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stagingFileCreated = true;
                writeStagingFile(stagingStream);
            }

            publishStagingFile(stagingPath, normalizedDestinationPath);
            published = true;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            if (stagingFileCreated && !published)
            {
                try
                {
                    deleteStagingFile(stagingPath);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
        }

        if (primaryFailure != null || cleanupFailure != null)
        {
            throw new IOException(
                BuildFailureMessage(normalizedDestinationPath, stagingPath, primaryFailure, cleanupFailure),
                CreateInnerException(primaryFailure, cleanupFailure));
        }
    }

    private static string BuildFailureMessage(
        string destinationPath,
        string stagingPath,
        Exception primaryFailure,
        Exception cleanupFailure)
    {
        return string.Format(
            "Atomic file write failed. destination={0} staging={1} primary={2} cleanup={3}",
            destinationPath ?? "(null)",
            stagingPath ?? "(null)",
            primaryFailure?.Message ?? "(none)",
            cleanupFailure?.Message ?? "(none)");
    }

    private static Exception CreateInnerException(
        Exception primaryFailure,
        Exception cleanupFailure)
    {
        if (primaryFailure == null)
        {
            return cleanupFailure;
        }
        if (cleanupFailure == null)
        {
            return primaryFailure;
        }
        return new AggregateException(
            "Atomic file write and staging cleanup both failed.",
            primaryFailure,
            cleanupFailure);
    }
}
