using BeMusicSeeker.TestSupport.Files;
using Ribbit.Media;
using Ribbit.Media.Audio;

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

    /// <summary>指定した検索先だけでencoderの有無を判定し、静的な検索先を書き換えず、WAVEは常に利用できる。</summary>
    /// <remarks>ケース所有の一時フォルダに空の実行ファイルを置いて区別し、既定の素材配置にflac.exeがない前提を使います。</remarks>
    [TestMethod]
    public void IsEncoderAvailable_UsesTheGivenDirectoryWithoutChangingTheStaticDirectory()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string withEncoder = Path.Combine(workspace.RootDirectory, "with-encoder");
        string empty = Path.Combine(workspace.RootDirectory, "empty");
        Directory.CreateDirectory(withEncoder);
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(withEncoder, EncoderType.FLAC.GetEncoderFileName()), string.Empty);
        string staticDirectory = BassAudioWriter.EncoderDirectory;

        Assert.IsTrue(BassAudioWriter.IsEncoderAvailable(EncoderType.FLAC, withEncoder));
        Assert.IsFalse(BassAudioWriter.IsEncoderAvailable(EncoderType.FLAC, empty));
        Assert.IsTrue(BassAudioWriter.IsEncoderAvailable(EncoderType.WAVE, empty));
        Assert.AreEqual(staticDirectory, BassAudioWriter.EncoderDirectory);
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
