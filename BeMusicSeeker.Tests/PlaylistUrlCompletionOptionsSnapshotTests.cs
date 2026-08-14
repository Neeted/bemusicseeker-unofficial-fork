using BeMusicSeeker.Properties;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistUrlCompletionOptionsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void CreateCurrentCapturesAllUrlCompletionSettings()
    {
        bool previousEnableCompletion = testSettings.EnablePlaylistUrlCompletion;
        string previousTsvUri = testSettings.PlaylistMd5UrlMappingTsvUri;
        bool previousEnableStella = testSettings.EnableStellaFullPlaylistUrlCompletion;
        bool previousOverwrite = testSettings.OverwritePlaylistUrlsWithCompletion;
        try
        {
            testSettings.EnablePlaylistUrlCompletion = true;
            testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.invalid/playlist.tsv";
            testSettings.EnableStellaFullPlaylistUrlCompletion = true;
            testSettings.OverwritePlaylistUrlsWithCompletion = true;

            PlaylistUrlCompletionOptionsSnapshot snapshot = PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings);

            Assert.IsTrue(snapshot.EnablePlaylistUrlCompletion);
            Assert.AreEqual("https://example.invalid/playlist.tsv", snapshot.PlaylistMd5UrlMappingTsvUri);
            Assert.IsTrue(snapshot.EnableStellaFullPlaylistUrlCompletion);
            Assert.IsTrue(snapshot.OverwritePlaylistUrlsWithCompletion);
        }
        finally
        {
            testSettings.EnablePlaylistUrlCompletion = previousEnableCompletion;
            testSettings.PlaylistMd5UrlMappingTsvUri = previousTsvUri;
            testSettings.EnableStellaFullPlaylistUrlCompletion = previousEnableStella;
            testSettings.OverwritePlaylistUrlsWithCompletion = previousOverwrite;
        }
    }
}
