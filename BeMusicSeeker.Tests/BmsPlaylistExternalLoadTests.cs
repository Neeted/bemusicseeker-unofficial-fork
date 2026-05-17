using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsPlaylistExternalLoadTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_BomHeaderAndRelativeDataUrl_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"Samalite難易度表\",\r\n\"symbol\":\"夏\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1,2]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Test Song\",\"artist\":\"Test Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = playlist.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual("Samalite難易度表", table.name);
            Assert.AreEqual("夏", table.symbol);
            Assert.AreEqual("./score.json", table.data_url);
            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.AreEqual("Test Song", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_BomHeaderAndRelativeDataUrl_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"Samalite難易度表\",\r\n\"symbol\":\"夏\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1,2]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Test Song\",\"artist\":\"Test Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual("Samalite難易度表", table.name);
            Assert.AreEqual("夏", table.symbol);
            Assert.AreEqual("./score.json", table.data_url);
            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.AreEqual("Test Song", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_HtmlWithoutHeaderMeta_ThrowsHeaderUriNotFound()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string htmlPath = Path.Combine(tempDirectory, "table.html");
            File.WriteAllText(htmlPath, "<html><head><title>No header</title></head><body>moved</body></html>", Encoding.UTF8);

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            Assert.ThrowsException<PlaylistHeaderUriNotFoundException>(() => playlist.LoadExternalTable(new Uri(htmlPath)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_Sha256OnlyEntry_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            string sha256 = new('a', 64);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"ShaOnly\",\"symbol\":\"S\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"sha256\":\"" + sha256 + "\",\"title\":\"Sha Song\",\"artist\":\"Sha Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = playlist.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.IsNull(table.entries.Single().md5);
            Assert.AreEqual(sha256, table.entries.Single().sha256);
            Assert.AreEqual("Sha Song", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_Md5AndSha256Entry_LoadsBothHashes()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DualHash\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Dual Song\",\"artist\":\"Dual Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = playlist.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", table.entries.Single().sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_InvalidSha256_IgnoresSha256AndKeepsRow()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"InvalidSha\",\"symbol\":\"I\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"invalid\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = playlist.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
            Assert.IsNull(table.entries.Single().sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadExternalTable_FullyEmptyEntry_IgnoresRow()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"EmptyRow\",\"symbol\":\"E\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"title\":\"Title Only\"},{\"title\":\"\",\"artist\":\"\",\"folder\":\"\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            var playlist = new BMSPlaylist(songDbPath);

            BMSTable table = playlist.LoadExternalTable(new Uri(headerJsonPath));

            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("Title Only", table.entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public async Task RegistrateExternalTableAsync_ExistingPlaylistNameThrowsSpecificException()
    {
        bool previousEnablePlaylistUrlCompletion = BeMusicSeeker.Properties.Settings.Default.EnablePlaylistUrlCompletion;
        BeMusicSeeker.Properties.Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DuplicateImport\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { new BMSTable { name = "DuplicateImport" } }),
                    Dispatcher.CurrentDispatcher)
            };

            PlaylistAlreadyExistsException ex = await Assert.ThrowsExceptionAsync<PlaylistAlreadyExistsException>(async delegate
            {
                await playlist.RegistrateExternalTableAsync(new Uri(headerJsonPath));
            });

            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Error_PlaylistAlreadyExists, ex.Message);
            Assert.AreEqual("DuplicateImport", ex.PlaylistName);
            Assert.AreEqual(1, playlist.BMSTables.Count);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempSongDbPath(string tempDirectory)
    {
        string sourceSongDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        File.Copy(sourceSongDbPath, tempSongDbPath, overwrite: true);
        return tempSongDbPath;
    }
}
