using BeMusicSeeker.TestSupport.Files;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

/// <summary>音声writerの出力path契約を検証します。</summary>
[TestClass]
public sealed class BassAudioWriterTests
{
    /// <summary>既存のmp3／wav出力と同名の保存先には衝突suffixを付け、元ファイルを上書きしない。</summary>
    [TestMethod]
    [DataRow(".mp3")]
    [DataRow(".wav")]
    public void OutputUsesTheExistingCollisionSuffixContract(string extension)
    {
        WithCollisionFixture(extension, path =>
        {
            Assert.AreEqual(
                path + " (2)" + extension,
                BassAudioWriter.GetAvailableOutputFile(path, extension));
        });
    }

    private static void WithCollisionFixture(string extension, Action<string> assertion)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = workspace.RootDirectory;
        string pathWithoutExtension = Path.Combine(directoryPath, "sample");

        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(pathWithoutExtension + extension, string.Empty);
        assertion(pathWithoutExtension);

    }
}
