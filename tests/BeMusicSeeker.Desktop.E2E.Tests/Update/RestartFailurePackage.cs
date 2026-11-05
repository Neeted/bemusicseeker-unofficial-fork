using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>
/// 更新後の再起動先として実行できない`BeMusicSeeker.exe`を持つ、手作りの更新packageです。
/// </summary>
/// <remarks>
/// Next ZIPの全entryを複製し、再起動先の実行ファイルだけをPEではないbytesへ置き換え、package専用のファイルを1件足します。
/// 更新確認は`sizeBytes`と`sha256`の一致だけを見るため、アプリ側の取得・検証・終了合意は通常どおり終わり、
/// 失敗は終了後の更新ツールが再起動先を起動する段階で起こります。本番に故障の注入口は設けません。
/// </remarks>
internal sealed class RestartFailurePackage
{
    private const string ExecutableEntry = "BeMusicSeeker.exe";
    private const string ManagedFilesEntry = "update-managed-files.txt";
    /// <summary>package専用ファイルの相対パスです。巻き戻し後にinstallationへ残ってはいけません。</summary>
    internal const string PackageOnlyFileName = "sc15c-package-only.txt";

    private RestartFailurePackage(string fileName, byte[] bytes)
    {
        FileName = fileName;
        Bytes = bytes;
    }

    /// <summary>配信とダイアログで使うファイル名です。Next ZIPのファイル名とは異なります。</summary>
    internal string FileName { get; }
    /// <summary>配るZIPの全bytesです。</summary>
    internal byte[] Bytes { get; }

    /// <summary>Next ZIPから壊れたpackageを作ります。前提が成り立たない場合は準備の失敗として例外にします。</summary>
    /// <param name="nextZipPath">元にするNext ZIPです。</param>
    /// <param name="nextVersion">Nextの版(四成分)です。ファイル名に含めます。</param>
    /// <param name="currentHashes">Current ZIPの各entryのSHA-256です。</param>
    /// <param name="nextHashes">Next ZIPの各entryのSHA-256です。</param>
    internal static async Task<RestartFailurePackage> CreateAsync(string nextZipPath, string nextVersion,
        IReadOnlyDictionary<string, string> currentHashes, IReadOnlyDictionary<string, string> nextHashes)
    {
        Assert.IsFalse(currentHashes.ContainsKey(PackageOnlyFileName) || nextHashes.ContainsKey(PackageOnlyFileName),
            "package専用ファイル名がCurrentまたはNextのentryと衝突しています。");
        byte[] brokenExecutable = Encoding.UTF8.GetBytes("SC-15C restart target is intentionally not executable " + Guid.NewGuid().ToString("N"));
        string brokenHash = Convert.ToHexStringLower(SHA256.HashData(brokenExecutable));
        Assert.AreNotEqual(currentHashes[ExecutableEntry], brokenHash, "壊れた実行ファイルがCurrentと同一です。");
        Assert.AreNotEqual(nextHashes[ExecutableEntry], brokenHash, "壊れた実行ファイルがNextと同一です。");
        byte[] bytes = await Task.Run(() => Build(nextZipPath, brokenExecutable)).ConfigureAwait(false);
        return new RestartFailurePackage($"bemusicseeker-unofficial-fork-v{nextVersion}-sc15c-restart-failure.zip", bytes);
    }

    private static byte[] Build(string nextZipPath, byte[] brokenExecutable)
    {
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (ZipArchive source = ZipFile.OpenRead(nextZipPath))
        {
            bool replaced = false;
            bool listed = false;
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                ZipArchiveEntry created = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                created.LastWriteTime = entry.LastWriteTime;
                if (entry.FullName.EndsWith('/')) { continue; }
                using Stream destination = created.Open();
                if (string.Equals(entry.FullName, ExecutableEntry, StringComparison.OrdinalIgnoreCase))
                {
                    destination.Write(brokenExecutable);
                    replaced = true;
                }
                else if (string.Equals(entry.FullName, ManagedFilesEntry, StringComparison.OrdinalIgnoreCase))
                {
                    // 更新ツールは一覧をZIPの実ファイル集合から書くが、手作りpackageを自己整合させるため追記する。
                    using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
                    string text = reader.ReadToEnd();
                    destination.Write(new UTF8Encoding(false).GetBytes(text.TrimEnd('\r', '\n') + "\n" + PackageOnlyFileName + "\n"));
                    listed = true;
                }
                else
                {
                    using Stream input = entry.Open();
                    input.CopyTo(destination);
                }
            }
            Assert.IsTrue(replaced && listed, "Next ZIPに実行ファイルまたは管理一覧のentryがありません。");
            ZipArchiveEntry packageOnly = target.CreateEntry(PackageOnlyFileName, CompressionLevel.Fastest);
            using Stream packageOnlyStream = packageOnly.Open();
            packageOnlyStream.Write(Encoding.UTF8.GetBytes("SC-15C package only " + Guid.NewGuid().ToString("N")));
        }
        return output.ToArray();
    }
}
