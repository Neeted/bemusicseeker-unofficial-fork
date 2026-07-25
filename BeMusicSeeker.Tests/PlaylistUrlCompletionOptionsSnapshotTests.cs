using BeMusicSeeker.Properties;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlaylistUrlCompletionOptionsSnapshotTests
{
    [TestMethod]
    public void CreateCurrentCapturesAllUrlCompletionSettings()
    {
        bool previousEnableCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        string previousTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
        bool previousEnableStella = Settings.Default.EnableStellaFullPlaylistUrlCompletion;
        bool previousOverwrite = Settings.Default.OverwritePlaylistUrlsWithCompletion;
        try
        {
            Settings.Default.EnablePlaylistUrlCompletion = true;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = "https://example.invalid/playlist.tsv";
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = true;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = true;

            PlaylistUrlCompletionOptionsSnapshot snapshot = PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default);

            Assert.IsTrue(snapshot.EnablePlaylistUrlCompletion);
            Assert.AreEqual("https://example.invalid/playlist.tsv", snapshot.PlaylistMd5UrlMappingTsvUri);
            Assert.IsTrue(snapshot.EnableStellaFullPlaylistUrlCompletion);
            Assert.IsTrue(snapshot.OverwritePlaylistUrlsWithCompletion);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnableCompletion;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = previousTsvUri;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = previousEnableStella;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = previousOverwrite;
        }
    }
}
