using System.IO;
using System.Security.Cryptography;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>SC02 の段階ごとの保存結果を raw readonly SQLite と実ファイルで検査します。</summary>
internal static class Sc02InstallOracle
{
    /// <summary>全所持譜面・資源情報・保留登録・全相対ファイルと元入力を独立期待値へ照合します。</summary>
    /// <remarks>実操作の成功・終端・pending/owned 表示と候補は、呼出元が本番 receipt と snapshot で確認します。</remarks>
    internal static void AssertState(Sc02InstallFixture fixture, Sc02InstallPhase phase)
    {
        Sc02PhaseExpectation expected = fixture.GetExpectedState(phase);
        AssertDirectoryFiles(fixture.LibraryRoot, expected.LibraryFiles);
        using (var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex))
        {
            Assert.AreEqual(expected.Charts.Count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song"), "song の所持集合が異なります。");
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song"), "この入力にない BMSON が登録されています。");
            Assert.AreEqual(expected.Charts.Count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance"), "所持の資源情報に欠落または余分な行があります。");
            CollectionAssert.AreEquivalent(expected.Charts.Select(chart => chart.Md5).Distinct(StringComparer.Ordinal).ToArray(),
                db.Query<ChartDigestKey>("SELECT md5 AS Md5 FROM chart_digest_map").Select(row => row.Md5).ToArray(),
                "chart_digest_map のキー集合に欠落または余分な行があります。");
            foreach (Sc02ChartManifest chart in expected.Charts)
            {
                string path = FullPath(fixture.LibraryRoot, chart.RelativePath);
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?", path), path);
                Assert.AreEqual(chart.Title, db.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?", path), path);
                Assert.AreEqual(chart.Md5, db.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?", path), path);
                Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5 = ?", chart.Md5), path);
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual(chart.Md5, db.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(chart.UsedAudioCount, db.ExecuteScalar<int?>("SELECT wav_files_defined FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(chart.UsedAudioCount, db.ExecuteScalar<int?>("SELECT wav_files_existing FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(0, db.ExecuteScalar<int?>("SELECT bga_files_defined FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(0, db.ExecuteScalar<int?>("SELECT bga_files_existing FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(0, db.ExecuteScalar<int?>("SELECT movie_files_defined FROM maintenance WHERE path = ?", path), path);
                Assert.AreEqual<int?>(0, db.ExecuteScalar<int?>("SELECT movie_files_existing FROM maintenance WHERE path = ?", path), path);
            }
            Assert.AreEqual(expected.PendingSourcePaths.Count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM install"), "install の保留元集合が異なります。");
            foreach (string path in expected.PendingSourcePaths)
            {
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM install WHERE path = ?", path), path);
            }
            Assert.AreEqual<int?>(Sc02InstallFixture.UnrelatedPendingDeleteParent, db.ExecuteScalar<int?>("SELECT delete_parent FROM install WHERE path = ?", fixture.UnrelatedPendingSourcePath), "無関係な保留元の設定が変わっています。");
            if (expected.PendingSourcePaths.Contains(fixture.NextDifferencePath, StringComparer.Ordinal))
            {
                Assert.AreEqual<int?>(Sc02InstallFixture.NextDifferenceDeleteParent, db.ExecuteScalar<int?>("SELECT delete_parent FROM install WHERE path = ?", fixture.NextDifferencePath), "次差分の保留元の設定が変わっています。");
            }
        }
        AssertFile(fixture.InitialArchivePath, fixture.InitialArchive);
        AssertDirectoryFiles(fixture.UnrelatedPendingSourcePath, fixture.UnrelatedPendingFiles);
        AssertFile(fixture.NextDifferencePath, fixture.NextDifference);
        if (expected.DifferenceConsumed)
        {
            foreach (Sc02FileManifest file in fixture.DifferenceFiles)
            {
                Assert.IsFalse(File.Exists(FullPath(fixture.DifferenceSourcePath, file.RelativePath)), "実移動で消費された差分元が残っています。" + file.RelativePath);
            }
            if (Directory.Exists(fixture.DifferenceSourcePath))
            {
                Assert.AreEqual(0, Directory.GetFiles(fixture.DifferenceSourcePath, "*", SearchOption.AllDirectories).Length, "差分入力に宣言していないファイルが残っています。");
            }
        }
        else { AssertDirectoryFiles(fixture.DifferenceSourcePath, fixture.DifferenceFiles); }
    }

    /// <summary>実 receipt の source/destination 対を使い、各期待ファイルの元消去と移動先 bytes を照合します。</summary>
    /// <remarks>ファイル単位とディレクトリ単位の receipt を受理し、対象範囲から相対位置を解決します。製品 DTO の構造は仮定しません。</remarks>
    internal static void AssertFileMoves(Sc02InstallFixture fixture, Sc02InstallPhase phase, IReadOnlyList<Sc02FileMoveObservation> moves)
    {
        IReadOnlyList<Sc02FileManifest> expectedFiles;
        string destinationRoot;
        if (phase == Sc02InstallPhase.InitialInstalled)
        {
            expectedFiles = fixture.InitialArchiveFiles;
            destinationRoot = fixture.InstalledWorkDirectory;
        }
        else if (phase == Sc02InstallPhase.DifferenceInstalled)
        {
            expectedFiles = fixture.DifferenceFiles;
            destinationRoot = fixture.ChildDirectory;
        }
        else { throw new ArgumentOutOfRangeException(nameof(phase), "物理導入の段階を指定してください。"); }
        Assert.IsTrue(moves.Count > 0, "成功した実 receipt の移動対象が必要です。");
        foreach (Sc02FileMoveObservation move in moves)
        {
            Assert.IsTrue(expectedFiles.Any(file => IsSameOrDescendant(FullPath(destinationRoot, file.RelativePath), Path.GetFullPath(move.DestinationPath))),
                "期待する導入対象を含まない receipt です。" + move.DestinationPath);
        }
        foreach (Sc02FileManifest file in expectedFiles)
        {
            string destination = FullPath(destinationRoot, file.RelativePath);
            Sc02FileMoveObservation[] matching = moves.Where(move => IsSameOrDescendant(destination, Path.GetFullPath(move.DestinationPath))).ToArray();
            Assert.AreEqual(1, matching.Length, "移動先を覆う receipt は一つである必要があります。" + destination);
            Sc02FileMoveObservation observed = matching[0];
            string destinationScope = Path.GetFullPath(observed.DestinationPath);
            string source = string.Equals(destination, destinationScope, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(observed.SourcePath)
                : FullPath(observed.SourcePath, Path.GetRelativePath(destinationScope, destination));
            if (phase == Sc02InstallPhase.DifferenceInstalled)
            {
                Assert.AreEqual(FullPath(fixture.DifferenceSourcePath, file.RelativePath), source, "差分元の実 receipt が異なります。");
            }
            Assert.IsFalse(File.Exists(source), "移動した元ファイルが残っています。" + source);
            AssertFile(destination, file);
        }
    }

    /// <summary>raw SELECT の結果を保持し、製品の保存行変換へ依存せずキー全体を照合します。</summary>
    private sealed class ChartDigestKey
    {
        /// <summary>DB に保存された MD5 です。不正な null も集合照合から除外しません。</summary>
        public string? Md5 { get; set; }
    }

    private static bool IsSameOrDescendant(string path, string scope) => string.Equals(path, scope, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(scope.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string FullPath(string root, string relativePath) => Path.GetFullPath(Path.Combine(root, relativePath));

    private static void AssertDirectoryFiles(string root, IReadOnlyList<Sc02FileManifest> expected)
    {
        Assert.IsTrue(Directory.Exists(root), root);
        string[] actualPaths = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        CollectionAssert.AreEquivalent(expected.Select(file => file.RelativePath.Replace('\\', '/')).ToArray(), actualPaths, root);
        foreach (Sc02FileManifest file in expected) { AssertFile(FullPath(root, file.RelativePath), file); }
    }

    private static void AssertFile(string path, Sc02FileManifest expected)
    {
        Assert.IsTrue(File.Exists(path), path);
        byte[] actual = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(expected.RawBytes.ToArray(), actual, path);
        Assert.AreEqual(expected.Md5, Convert.ToHexStringLower(MD5.HashData(actual)), path);
        Assert.AreEqual(expected.Sha256, Convert.ToHexStringLower(SHA256.HashData(actual)), path);
    }
}

/// <summary>実 receipt から呼出元が捕捉する物理移動の確定対です。成功対象以外を混ぜません。</summary>
/// <param name="SourcePath">実 receipt が返した移動元ファイルまたはディレクトリの絶対 path です。</param>
/// <param name="DestinationPath">対応する実移動先の絶対 path です。</param>
internal sealed record Sc02FileMoveObservation(string SourcePath, string DestinationPath);
