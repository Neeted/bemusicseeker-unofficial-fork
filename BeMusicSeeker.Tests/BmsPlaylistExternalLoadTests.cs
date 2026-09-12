using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsPlaylistExternalLoadTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public void CreateBMSTable_UsesPlaylistDefaultIgnoreFolderOutputSetting()
    {
        int previousDefault = BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput;
        BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput =
            (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };

            BMSTable table = playlist.CreateBMSTable();

            Assert.AreEqual(LR2SongDBExtended.playlist.CustomFolderType.AllFolders, table.ignore_folder_output);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput = previousDefault;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public async Task LoadExternalTableAsync_NewExternalTableUsesPlaylistDefaultIgnoreFolderOutputSetting()
    {
        int previousDefault = BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput;
        var expectedMask = LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder;
        BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput = (int)expectedMask;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"DefaultMaskImport\",\"symbol\":\"D\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual(expectedMask, table.ignore_folder_output);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput = previousDefault;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    [DoNotParallelize]
    public async Task LoadExternalTableAsync_BaseTablePreservesIgnoreFolderOutput()
    {
        int previousDefault = BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput;
        BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput =
            (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
        var expectedMask = LR2SongDBExtended.playlist.CustomFolderType.UserFolder
            | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"PreserveMaskImport\",\"symbol\":\"P\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);
            var baseTable = new BMSTable
            {
                ignore_folder_output = expectedMask
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(expectedMask, table.ignore_folder_output);
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.PlaylistDefaultIgnoreFolderOutput = previousDefault;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

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

    /// <summary>取得済みHTMLの参照先を読み込み、解析による再取得や外部DTD取得がないことを確認します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LoadExternalTableAsync_UsesFetchedHtmlWithoutParserNetworkRequests()
    {
        string parserFallbackHtml =
            "<html><head><meta name=\"bmstable\" content=\"../headers/alternate.json\"></head>"
            + "<body>table B</body></html>";
        await using var parserNetworkServer = new SingleRequestHttpServer(Encoding.UTF8.GetBytes(parserFallbackHtml));

        Uri pageUri = new(parserNetworkServer.Address, "pages/start.html");
        Uri headerUri = new(parserNetworkServer.Address, "headers/main.json");
        Uri dataUri = new(parserNetworkServer.Address, "data/rows.json");
        Uri alternateHeaderUri = new(parserNetworkServer.Address, "headers/alternate.json");
        Uri alternateDataUri = new(parserNetworkServer.Address, "data/alternate.json");
        Uri externalDtdUri = new(parserNetworkServer.Address, "doctype/table.dtd");

        string pageHtml = "<!DOCTYPE html SYSTEM \"" + externalDtdUri.AbsoluteUri + "\">"
            + "<html><head><meta content=\"../headers/main.json\" name=\"bmstable\"></head>"
            + "<body>table A</body></html>";
        var responses = new Dictionary<Uri, string>
        {
            [pageUri] = pageHtml,
            [headerUri] = "{\"name\":\"Table A\",\"symbol\":\"A\",\"data_url\":\"../data/rows.json\"}",
            [dataUri] = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"artist\":\"Artist A\",\"level\":\"1\"}]",
            [alternateHeaderUri] = "{\"name\":\"Table B\",\"symbol\":\"B\",\"data_url\":\"../data/alternate.json\"}",
            [alternateDataUri] = "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"artist\":\"Artist B\",\"level\":\"2\"}]"
        };
        using var handler = new RecordedExternalTableHttpMessageHandler(responses);
        using var transport = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var appHttpClient = new AppHttpClient(transport, TimeProvider.System);
        var recommendedTableOwner = new PlaylistRecommendedTableOwner(
            string.Empty,
            static () => [],
            static (_, _) => Task.FromException<BMSTable>(new InvalidOperationException("The bmseeker route is not part of this fixture.")),
            new AppPlaylistRecommendedTableHttpClient(appHttpClient),
            new PlaylistOperationNotificationOwner(),
            static () => new CustomFolderOutputSettingsSnapshot());
        var externalOwner = new PlaylistExternalSyncOwner(
            appHttpClient,
            recommendedTableOwner,
            (_, _) => { },
            static () => false,
            static _ => { },
            customFolderOutputSettingsProvider: static () => new CustomFolderOutputSettingsSnapshot());

        BMSTable table = await externalOwner.LoadExternalTableAsync(pageUri);

        Assert.AreEqual("Table A", table.name);
        Assert.AreEqual("A", table.symbol);
        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", table.entries.Single().md5);
        Assert.AreEqual("Song A", table.entries.Single().title);
        Assert.AreEqual(pageUri, table.Page_url);
        Assert.AreEqual(headerUri, table.GetAbsoluteHeaderUrl());
        Assert.AreEqual(dataUri, table.GetAbsoluteDataUrl());
        Assert.AreEqual(1, handler.GetRequestCount(pageUri));
        Assert.AreEqual(1, handler.GetRequestCount(headerUri));
        Assert.AreEqual(1, handler.GetRequestCount(dataUri));
        Assert.AreEqual(3, handler.RequestUris.Count);
        Assert.AreEqual(string.Empty, parserNetworkServer.RequestMethod, parserNetworkServer.Diagnostics);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTable_ExternalDataUrlResolutionPreservesDriveFileAndUncRoutes()
    {
        var driveTable = new BMSTable
        {
            Header_url = new Uri("file:///C:/tables/header.json"),
            Data_url = new Uri("../data/score.json", UriKind.Relative)
        };

        Uri driveDataUri = driveTable.GetAbsoluteDataUrl();

        Assert.AreEqual("file", driveDataUri.Scheme);
        Assert.AreEqual("C:/data/score.json", driveDataUri.AbsolutePath);

        var uncTable = new BMSTable
        {
            Header_url = new Uri("file://server/share/tables/header.json"),
            Data_url = new Uri("../data/score.json", UriKind.Relative)
        };

        Uri uncDataUri = uncTable.GetAbsoluteDataUrl();

        Assert.AreEqual("file", uncDataUri.Scheme);
        Assert.AreEqual("server", uncDataUri.Host);
        Assert.AreEqual("/share/data/score.json", uncDataUri.AbsolutePath);
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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            Assert.ThrowsException<PlaylistHeaderUriNotFoundException>(() => playlist.ExternalSyncOwner.LoadExternalTable(new Uri(htmlPath)));
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
    public void LoadExternalTable_UnclosedHtmlWithReorderedMetaAttributes_LoadsSuccessfully()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string htmlPath = Path.Combine(tempDirectory, "table.html");
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllText(
                htmlPath,
                "<html><head><meta content=\"header.json\" name=\"bmstable\"><title>unfinished",
                Encoding.UTF8);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes(
                "{\"name\":\"UnclosedHtml\",\"symbol\":\"U\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes(
                "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(htmlPath));

            Assert.AreEqual("UnclosedHtml", table.name);
            Assert.AreEqual("U", table.symbol);
            Assert.AreEqual(1, table.entries.Count);
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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

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

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));

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

    [DataTestMethod]
    [TestCategory("Playlist")]
    [DataRow("", "1")]
    [DataRow("LEVEL ", "LEVEL 1")]
    [DataRow("😀", "😀1")]
    public async Task LoadExternalTableAsync_BaseTableCompatPrefixIsPreservedWhenHeaderOmitsCompatPrefix(string compatPrefix, string expectedFolder)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = compatPrefix
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(compatPrefix, table.compat_prefix);
            Assert.AreEqual(expectedFolder, table.entries.Single().folder);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [TestCategory("Playlist")]
    [DataRow("", "1")]
    [DataRow("LOCAL ", "LOCAL 1")]
    public async Task LoadExternalTableAsync_BaseTableCompatPrefixIsPreservedWhenHeaderDefinesCompatPrefix(string compatPrefix, string expectedFolder)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = compatPrefix
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            Assert.AreEqual(compatPrefix, table.compat_prefix);
            Assert.AreEqual(expectedFolder, table.entries.Single().folder);
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
    public async Task LoadExternalTableAsync_HeaderFolderOrderIsRewrittenWhenCompatPrefixIsPreserved()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"folder_order\":[\"EXTERNAL 2\",\"EXTERNAL 1\"]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song 1\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song 2\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);
            var baseTable = new BMSTable
            {
                name = "Existing",
                symbol = "ExistingSymbol",
                compat_prefix = "LOCAL "
            };

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), baseTable);

            CollectionAssert.AreEqual(new[] { "LOCAL 2", "LOCAL 1" }, table.Folder_order);
            CollectionAssert.AreEqual(new[] { "LOCAL 2", "LOCAL 1" }, table.folder_list);
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
    public async Task LoadExternalTableAsync_HeaderHashIgnoresCompatPrefix()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"EXTERNAL \",\"data_url\":\"./score.json\",\"folder_order\":[\"EXTERNAL 1\"]}"));
            BMSTable first = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Reloaded\",\"symbol\":\"st\",\"compat_prefix\":\"CHANGED \",\"data_url\":\"./score.json\",\"folder_order\":[\"CHANGED 1\"]}"));
            BMSTable second = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath), first);

            Assert.AreEqual(first.header_sha256, second.header_sha256);
            Assert.AreEqual(first.compat_prefix, second.compat_prefix);
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
    public async Task LoadExternalTableAsync_InferCompatPrefixFromHeaderSymbolWhenHeaderOrderIsMissing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"./score.json\"}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"0\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath);

            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));

            Assert.AreEqual("st", table.compat_prefix);
            Assert.AreEqual("st0", table.entries.Single().folder);
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
    public async Task RegistrateExternalTableAsync_NonCp932TagPersistsLegacyPrefix()
    {
        bool previousEnablePlaylistUrlCompletion = BeMusicSeeker.Properties.Settings.Default.EnablePlaylistUrlCompletion;
        BeMusicSeeker.Properties.Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistExternalLoadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\"name\":\"UnicodeImport\",\"symbol\":\"st\",\"tag\":\"😀\",\"data_url\":\"./score.json\",\"level_order\":[1]}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(new Uri(headerJsonPath));

            BMSTable table = playlist.BMSTables.Single();
            Assert.AreEqual("LEVEL ", table.compat_prefix);
            Assert.AreEqual("LEVEL 1", table.entries.Single().folder);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == table.playlist_id);
            Assert.AreEqual("LEVEL ", persisted.compat_prefix);
            Assert.AreEqual("LEVEL 1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ?;", table.playlist_id));
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { new BMSTable { name = "DuplicateImport" } })
            };

            PlaylistAlreadyExistsException ex = await Assert.ThrowsExceptionAsync<PlaylistAlreadyExistsException>(async delegate
            {
                await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(new Uri(headerJsonPath));
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
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        using (var _ = new LR2SongDBExtended(tempSongDbPath))
        {
        }
        PlaylistPersistenceRepository.EnsureSchema(tempSongDbPath);
        return tempSongDbPath;
    }

    private sealed class RecordedExternalTableHttpMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<Uri, string> responses;
        private readonly List<Uri> requestUris = [];

        /// <summary>要求先ごとの合成応答を設定し、アプリ側の取得を記録します。</summary>
        internal RecordedExternalTableHttpMessageHandler(IReadOnlyDictionary<Uri, string> responses)
        {
            this.responses = responses ?? throw new ArgumentNullException(nameof(responses));
        }

        /// <summary>アプリのHTTP経路が取得したURIの記録です。</summary>
        internal IReadOnlyList<Uri> RequestUris => requestUris;

        /// <summary>指定URIがアプリ側で取得された回数を返します。</summary>
        internal int GetRequestCount(Uri uri)
        {
            return requestUris.Count(requestUri => requestUri == uri);
        }

        /// <summary>実通信せず合成応答を返し、未定義の取得先と取消しを失敗として伝えます。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("HTTP request URI is missing.");
            requestUris.Add(requestUri);
            if (!responses.TryGetValue(requestUri, out string? responseBody) || responseBody is null)
            {
                throw new HttpRequestException("Unexpected external table request: " + requestUri);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
