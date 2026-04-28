using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    [TestMethod]
    public void ApplyNeedToBeFixedWarnings_AppendsMissingResourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 10,
            wav_files_existing = 5,
            is_stagefile_defined = true,
            is_stagefile_existing = false
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

        bool needsFix = service.ApplyNeedToBeFixedWarnings(file, info);

        Assert.IsTrue(needsFix);
        StringAssert.Contains(file.warning, string.Format(Resources.Warning_WavFilesNotFound, info.GetWAVHealth(), 5, 10));
        StringAssert.Contains(file.warning, Resources.Warning_StagefileNotFound);
    }

    [TestMethod]
    public void GetZeroNoteFiles_FiltersOnlyZeroNoteCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetNotes(0);
        TestableBmsFile normalFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        normalFile.SetNotes(1200);
        PendingChartEntry bmsonRow = CreateBmsonRow("C:\\Library\\chart.bmson", "cccccccccccccccccccccccccccccccc");
        SetNotes(bmsonRow, 0);

        List<BMSFile> result = service.GetZeroNoteFiles(new BMSFile[] { zeroNoteFile, normalFile, bmsonRow });

        CollectionAssert.AreEqual(new[] { zeroNoteFile }, result);
    }

    [TestMethod]
    public void BmsOnlyMaintenanceOperations_SkipBmsonPendingRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        PendingChartEntry bmsonRow = CreateBmsonRow("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        string originalTitle = bmsonRow.Title;
        string originalArtist = bmsonRow.Artist;
        bmsonRow.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsonRow)
        {
            hash = bmsonRow.hash,
            encoding = "gb2312",
            is_encoding_fixed = false,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        SetNotes(bmsonRow, 0);

        Assert.IsFalse(service.ApplyNeedToBeFixedWarnings(bmsonRow));
        Assert.AreEqual(0, service.GetGarbledFiles(new BMSFile[] { bmsonRow }, isInFixedList: false).Count);
        Assert.AreEqual(0, service.GetZeroNoteFiles(new BMSFile[] { bmsonRow }).Count);
        Assert.AreEqual(0, service.SetFilesWarningIgnored(new BMSFile[] { bmsonRow }, unset: false).Count);

        MaintenanceEncodingUpdateResult encodingResult = service.ApplyEncoding(new BMSFile[] { bmsonRow }, "shift_jis");

        Assert.AreEqual(0, encodingResult.SongsToUpsert.Count);
        Assert.AreEqual(0, encodingResult.MaintenanceInfosToUpsert.Count);
        Assert.AreEqual(originalTitle, bmsonRow.Title);
        Assert.AreEqual(originalArtist, bmsonRow.Artist);
        Assert.AreEqual("gb2312", bmsonRow.maintenanceInfo.encoding);
        Assert.IsFalse(bmsonRow.maintenanceInfo.is_encoding_fixed);
    }

    [TestMethod]
    public void SetFilesWarningIgnored_TogglesOnlyMatchingEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            is_files_warning_ignored = false
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

        List<BMSFileMaintenanceInfo> changes = service.SetFilesWarningIgnored(new BMSFile[] { file }, unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(info.is_files_warning_ignored);
    }

    [TestMethod]
    public void GetGarbledFiles_IncludesUnknownEncodingInRegularAndFixedLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile unknownFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        unknownFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(unknownFile)
        {
            hash = unknownFile.hash,
            encoding = "unknown",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile fixedUnknownFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        fixedUnknownFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(fixedUnknownFile)
        {
            hash = fixedUnknownFile.hash,
            encoding = "unknown",
            is_encoding_fixed = true
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile shiftJisFile = CreateFile("cccccccccccccccccccccccccccccccc");
        shiftJisFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(shiftJisFile)
        {
            hash = shiftJisFile.hash,
            encoding = "shift_jis",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile shiftJisQuestionFile = CreateFile("dddddddddddddddddddddddddddddddd");
        shiftJisQuestionFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(shiftJisQuestionFile)
        {
            hash = shiftJisQuestionFile.hash,
            encoding = "shift_jis?",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile gb2312File = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        gb2312File.SetMaintenanceInfo(new BMSFileMaintenanceInfo(gb2312File)
        {
            hash = gb2312File.hash,
            encoding = "gb2312",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile big5File = CreateFile("ffffffffffffffffffffffffffffffff");
        big5File.SetMaintenanceInfo(new BMSFileMaintenanceInfo(big5File)
        {
            hash = big5File.hash,
            encoding = "big5",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);

        List<BMSFile> regularList = service.GetGarbledFiles(new BMSFile[] { unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File }, isInFixedList: false);
        List<BMSFile> fixedList = service.GetGarbledFiles(new BMSFile[] { unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File }, isInFixedList: true);

        CollectionAssert.AreEquivalent(new BMSFile[] { unknownFile, gb2312File, big5File }, regularList);
        CollectionAssert.AreEquivalent(new BMSFile[] { fixedUnknownFile }, fixedList);
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_SkipsMissingFilesAndClearsWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetNotes(0);
        zeroNoteFile.path = "C:\\missing\\chart.bms";
        zeroNoteFile.HasZeroNoteMismatchWarning = true;

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(new BMSFile[] { zeroNoteFile });

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.IsFalse(zeroNoteFile.HasZeroNoteMismatchWarning);
    }

    [TestMethod]
    public void ApplyEncoding_ReloadsWhenEncodingMatchesButDecodedMetadataDiffers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "同一Encoding再読込";
        const string expectedArtist = "差分あり";
        const string expectedGenre = "GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle("stale");
            file.SetArtist("stale");
            file.SetGenre("stale");
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "gb2312",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding(new BMSFile[] { file }, "gb2312");

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(expectedGenre, file.genre);
            CollectionAssert.AreEqual(new BMSFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_SkipsSongReloadWhenDecodedMetadataMatchesButUpdatesEncoding()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "一致タイトル";
        const string expectedArtist = "一致アーティスト";
        const string expectedGenre = "一致GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding(new BMSFile[] { file }, "gb2312");

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(expectedGenre, file.genre);
            Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(0, result.SongsToUpsert.Count);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_RaisesEncodingPropertyChangedWhenEncodingOnlyChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "一致タイトル";
        const string expectedArtist = "一致アーティスト";
        const string expectedGenre = "一致GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);
            List<string> propertyNames = new List<string>();
            file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding(new BMSFile[] { file }, "gb2312");

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            CollectionAssert.AreEqual(new[] { info }, result.MaintenanceInfosToUpsert);
            CollectionAssert.Contains(propertyNames, "encoding");
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_SkipsEntireUpdateWhenDecodedMetadataAndEncodingMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "完全一致";
        const string expectedArtist = "一致";
        const string expectedGenre = "MATCH";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "gb2312",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding(new BMSFile[] { file }, "gb2312");

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            Assert.AreEqual(0, result.MaintenanceInfosToUpsert.Count);
            Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
            Assert.IsFalse(file.maintenanceInfo.is_encoding_fixed);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_RaisesHealthPropertyChangedWhenHealthChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#STAGEFILE missing.png\r\n#BANNER missing_banner.png\r\n#BACKBMP missing_back.bmp\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            BMSFile file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash
            }, suppressPropertyChanged: true, registerEventHandlers: false);
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(tempDirectoryPath, Directory.GetFiles(tempDirectoryPath));
            List<string> propertyNames = new List<string>();
            file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: true,
                folderHash,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            CollectionAssert.Contains(propertyNames, "StagefileHealth");
            CollectionAssert.Contains(propertyNames, "BannerHealth");
            CollectionAssert.Contains(propertyNames, "BackbmpHealth");
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow("gb2312", "\u7b80\u4f53\u6807\u9898", "\u7b80\u4f53\u4f5c\u8005")]
    [DataRow("big5", "\u7e41\u9ad4\u6a19\u984c", "\u7e41\u9ad4\u4f5c\u8005")]
    [DataRow("ks_c_5601-1987", "\ud55c\uad6d\uc5b4\uc81c\ubaa9", "\ud55c\uad6d\uc5b4\uc791\uac00")]
    public void ApplyEncoding_ReloadsRequestedEncodingAndMarksFixed(string encoding, string expectedTitle, string expectedArtist)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsContent = "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE TEST\r\n";
        File.WriteAllText(
            bmsFilePath,
            bmsContent,
            Encoding.GetEncoding(encoding, new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding(new BMSFile[] { file }, encoding);

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(encoding, file.maintenanceInfo.encoding);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
            CollectionAssert.AreEqual(new BMSFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.SetHash(hash);
        return file;
    }

    private static PendingChartEntry CreateBmsonRow(string path, string md5)
    {
        LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
        {
            path = path,
            folder = Path.GetDirectoryName(path),
            title = "Bmson Title",
            artist = "Bmson Artist",
            md5 = md5,
            sha256 = new string('b', 64)
        };
        return PendingChartEntry.CreateFromBmsonSong(song);
    }

    private static void SetNotes(BMSFile file, int? value)
    {
        typeof(BMSFile).GetProperty(nameof(BMSFile.notes)).GetSetMethod(nonPublic: true).Invoke(file, new object[] { value });
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetNotes(int? value)
        {
            notes = value;
        }

        public void SetTitle(string value)
        {
            Title = value;
        }

        public void SetArtist(string value)
        {
            Artist = value;
        }

        public void SetGenre(string value)
        {
            genre = value;
        }
    }
}
