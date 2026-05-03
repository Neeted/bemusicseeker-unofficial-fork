using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    [TestMethod]
    public void ApplyNeedToBeFixedWarnings_SetsStructuredMissingResourceWarnings()
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
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceStagefileMissing));
        StringAssert.Contains(file.WarningTooltipText, string.Format(Resources.Warning_WavFilesNotFound, info.GetWAVHealth(), 5, 10));
        StringAssert.Contains(file.WarningTooltipText, Resources.Warning_StagefileNotFound);
        Assert.AreEqual("[2] リソース不足", file.WarningDigestText);
    }

    [TestMethod]
    public void BuildResourceHealthWarnings_DoesNotMutateSourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 2,
            wav_files_existing = 1
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(file, info);

        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(ChartWarningKind.ResourceWavMissing, warnings[0].Kind);
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, file.WarningDigestText);
    }

    [TestMethod]
    public void LibraryChartRow_ProjectsResourceHealthWarningsWithoutMutatingSource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "stale resource warning");
        BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            bga_files_defined = 4,
            bga_files_existing = 3
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        row.SetResourceHealthProjectionProvider(_ => new ResourceHealthWarningProjection(1, service.BuildResourceHealthWarnings(file, info), isIgnored: false));

        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
        Assert.IsFalse(row.WarningTooltipText.Contains("stale resource warning"));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));

        file.instl_dst = @"C:\Installed";
        Assert.IsFalse(row.WarningDigestText.Contains(Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
    }

    [TestMethod]
    public void LibraryChartRow_ResourceProjectionSuppressesStaleSourceResourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "pending resource warning");
        file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 2,
            wav_files_existing = 2
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        row.SetResourceHealthProjectionProvider(_ => ResourceHealthWarningProjection.Empty);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, row.WarningDigestText);
        Assert.IsFalse(row.WarningTooltipText.Contains("pending resource warning"));
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_GroupsActiveAndIgnoredWithoutMutatingWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active.path = @"C:\Library\active.bms";
        active.SetMaintenanceInfo(new BMSFileMaintenanceInfo(active)
        {
            hash = active.hash,
            wav_files_defined = 2,
            wav_files_existing = 1,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        TestableBmsFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored.path = @"C:\Library\ignored.bms";
        ignored.SetMaintenanceInfo(new BMSFileMaintenanceInfo(ignored)
        {
            hash = ignored.hash,
            bga_files_defined = 2,
            bga_files_existing = 1,
            is_files_warning_ignored = true
        }, suppressPropertyChanged: true, registerEventHandlers: false);

        ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build(new BMSFile[] { active, ignored }, service, version: 3);

        CollectionAssert.AreEqual(new[] { active }, snapshot.ActiveFiles.ToArray());
        CollectionAssert.AreEqual(new[] { ignored }, snapshot.IgnoredFiles.ToArray());
        Assert.IsTrue(snapshot.GetProjection(active).HasIssues);
        Assert.IsFalse(snapshot.GetProjection(active).IsIgnored);
        Assert.IsTrue(snapshot.GetProjection(ignored).IsIgnored);
        Assert.IsFalse(active.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.IsFalse(ignored.Warnings.Contains(ChartWarningKind.ResourceBgaMissing));
    }

    [TestMethod]
    public void ApplyNeedToBeFixedWarnings_ReplacesOnlyResourceHealthWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "install estimate");
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "stale wav");
        BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            bga_files_defined = 4,
            bga_files_existing = 3
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true, registerEventHandlers: false);

        bool needsFix = service.ApplyNeedToBeFixedWarnings(file, info);

        Assert.IsTrue(needsFix);
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceBgaMissing));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void GetZeroNoteFiles_FiltersOnlyZeroNoteCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetNotes(1200);
        zeroNoteFile.SetChartInfo(CreateChartInfo(zeroNoteFile.hash, notes: 0));
        TestableBmsFile normalFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        normalFile.SetNotes(0);
        normalFile.SetChartInfo(CreateChartInfo(normalFile.hash, notes: 1200));
        TestableBmsFile missingChartInfoFile = CreateFile("dddddddddddddddddddddddddddddddd");
        missingChartInfoFile.SetNotes(0);
        PendingChartEntry bmsonRow = CreateBmsonRow("C:\\Library\\chart.bmson", "cccccccccccccccccccccccccccccccc");
        SetNotes(bmsonRow, 0);
        bmsonRow.SetChartInfo(CreateChartInfo(bmsonRow.hash, notes: 0));

        List<BMSFile> result = service.GetZeroNoteFiles(new BMSFile[] { zeroNoteFile, normalFile, missingChartInfoFile, bmsonRow });

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
            is_files_warning_ignored = false,
            wav_files_defined = 1,
            wav_files_existing = 0
        }, suppressPropertyChanged: true, registerEventHandlers: false);
        SetNotes(bmsonRow, 0);

        Assert.IsTrue(service.ApplyNeedToBeFixedWarnings(bmsonRow));
        Assert.AreEqual(0, service.GetGarbledFiles(new BMSFile[] { bmsonRow }, isInFixedList: false).Count);
        Assert.AreEqual(0, service.GetZeroNoteFiles(new BMSFile[] { bmsonRow }).Count);
        Assert.AreEqual(1, service.SetFilesWarningIgnored(new BMSFile[] { bmsonRow }, unset: false).Count);

        MaintenanceEncodingUpdateResult encodingResult = service.ApplyEncoding(new BMSFile[] { bmsonRow }, "shift_jis");

        Assert.AreEqual(0, encodingResult.SongsToUpsert.Count);
        Assert.AreEqual(0, encodingResult.MaintenanceInfosToUpsert.Count);
        Assert.AreEqual(originalTitle, bmsonRow.Title);
        Assert.AreEqual(originalArtist, bmsonRow.Artist);
        Assert.AreEqual("gb2312", bmsonRow.maintenanceInfo.encoding);
        Assert.IsFalse(bmsonRow.maintenanceInfo.is_encoding_fixed);
        Assert.IsTrue(bmsonRow.maintenanceInfo.is_files_warning_ignored);
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
    public void CreateFromBmsonSong_SetsMaintenanceHashAndPath()
    {
        PendingChartEntry bmsonRow = CreateBmsonRow("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.AreEqual(bmsonRow.hash, bmsonRow.maintenanceInfo.hash);
        Assert.AreEqual(bmsonRow.path, bmsonRow.maintenanceInfo.path);
        Assert.AreEqual("utf-8", bmsonRow.maintenanceInfo.encoding);
        Assert.IsFalse(bmsonRow.maintenanceInfo.is_encoding_fixed);
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
        zeroNoteFile.SetChartInfo(CreateChartInfo(zeroNoteFile.hash, notes: 0));
        zeroNoteFile.path = "C:\\missing\\chart.bms";
        zeroNoteFile.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(new BMSFile[] { zeroNoteFile });

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(zeroNoteFile.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
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

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonResourceHealth_UpsertsMaintenanceOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            PendingChartEntry file = PendingChartEntry.CreateFromBmsonSong(BmsonSongParser.Parse(bmsonFilePath));
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: true,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            Assert.AreEqual("Bmson", file.Title);
            Assert.AreEqual("Artist", file.Artist);
            Assert.AreEqual("utf-8", file.maintenanceInfo.encoding);
            Assert.IsFalse(file.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(1, file.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, file.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(true, file.maintenanceInfo.is_stagefile_defined);
            Assert.AreEqual(false, file.maintenanceInfo.is_stagefile_existing);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, songDb.Table<BMSFileMaintenanceInfo>().Count());
                Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
            }
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
    public void UpdateMaintenanceInfo_BmsonUsesFreshResourceReferencesWithoutReparse()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            DateTime timestamp = song.updated_at;
            PendingChartEntry file = PendingChartEntry.CreateFromBmsonSong(song);
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, timestamp);
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: false,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, file.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, file.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(true, file.maintenanceInfo.is_stagefile_defined);
            Assert.AreEqual(false, file.maintenanceInfo.is_stagefile_existing);
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
    public void UpdateMaintenanceInfo_BmsonFreshReferencesAreStaleWhenTimestampChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            PendingChartEntry file = PendingChartEntry.CreateFromBmsonSong(BmsonSongParser.Parse(bmsonFilePath));
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, file.BmsonSong.updated_at.AddMinutes(1));
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: false,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
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
    public void UpdateMaintenanceInfo_BmsonNonFreshRowReparsesResourceReferences()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            LR2SongDBExtended.bmson_song parsed = BmsonSongParser.Parse(bmsonFilePath);
            LR2SongDBExtended.bmson_song loadedLikeRow = new LR2SongDBExtended.bmson_song
            {
                path = parsed.path,
                folder = parsed.folder,
                title = parsed.title,
                md5 = parsed.md5,
                sha256 = parsed.sha256,
                updated_at = parsed.updated_at
            };
            PendingChartEntry file = PendingChartEntry.CreateFromBmsonSong(loadedLikeRow);
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: false,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, file.maintenanceInfo.wav_files_defined);
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
    public void UpdateMaintenanceInfo_BmsonForceUpdateReparsesFreshResourceReferences()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            PendingChartEntry file = PendingChartEntry.CreateFromBmsonSong(BmsonSongParser.Parse(bmsonFilePath));
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { file },
                forceUpdate: true,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
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
    public void UpdateMaintenanceInfo_MixedBmsAndBmsonResourceHealth_ScansBothButDoesNotCreateBmsonSongRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#ARTIST Artist\r\n#WAV01 missing-bms.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing-bmson.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            BMSFile bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            PendingChartEntry bmsonFile = PendingChartEntry.CreateFromBmsonSong(BmsonSongParser.Parse(bmsonFilePath));
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(tempDirectoryPath, Directory.GetFiles(tempDirectoryPath));
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new BMSFile[] { bmsFile, bmsonFile },
                forceUpdate: true,
                folderHash,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual("Bmson", bmsonFile.Title);
            Assert.AreEqual("Artist", bmsonFile.Artist);
            Assert.AreEqual("utf-8", bmsonFile.maintenanceInfo.encoding);
            Assert.AreEqual(1, bmsonFile.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, bmsonFile.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, bmsFile.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, bmsFile.maintenanceInfo.wav_files_existing);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<BMSFileMaintenanceInfo>().Count());
                Assert.IsFalse(songDb.Table<LR2SongDB.song>().ToList().Any((LR2SongDB.song song) => song.hash == bmsonFile.hash));
            }
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
    public void ResolveDefaultMaintenanceHealthDegree_UsesProcessorCountMinusOne()
    {
        Assert.AreEqual(Math.Max(1, Environment.ProcessorCount - 1), BmsLibraryMaintenanceService.ResolveDefaultMaintenanceHealthDegree());
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_UsesMaintenanceHealthDegreeOverride()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService(0);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            BMSFile bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new BMSFile[] { bmsFile },
                forceUpdate: true,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.AreEqual(1, result.HealthDegree);
            Assert.AreEqual(1, result.HealthTargetCount);
            Assert.IsTrue(result.HealthMs >= 0);
            Assert.IsTrue(result.EncodingMs >= 0);
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
    public void UpdateMaintenanceInfo_NoTargetsReportsSummaryAndSkipsWork()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        List<string> logs = new List<string>();
        try
        {
            BMSFile bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            bmsFile.maintenanceInfo.wav_files_defined = 0;
            bmsFile.maintenanceInfo.bga_files_defined = 0;
            bmsFile.maintenanceInfo.movie_files_defined = 0;
            bmsFile.maintenanceInfo.is_stagefile_defined = false;
            bmsFile.maintenanceInfo.is_backbmp_defined = false;
            bmsFile.maintenanceInfo.is_banner_defined = false;
            bmsFile.maintenanceInfo.encoding = "shift_jis";
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new BMSFile[] { bmsFile },
                forceUpdate: false,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null,
                progressLogger: logs.Add);

            Assert.AreEqual(0, result.CheckedFileCount);
            Assert.AreEqual(0, result.HealthTargetCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.IsTrue(logs.Any((string log) => log.Contains("maintenance_target_summary") && log.Contains("total=0")));
            Assert.IsTrue(logs.Any((string log) => log.Contains("maintenance_update no_targets")));
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
    public void UpdateMaintenanceInfo_CacheAwareHealthMatchesFileExistsPathAndAvoidsFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "audio"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "image"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "movie"));
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "audio", "hit.ogg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "image", "pic.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "movie", "clip.mp4"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "stage.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "banner.bmp"), "");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 audio\\hit.wav\r\n"
            + "#WAV02 audio\\missing.wav\r\n"
            + "#BMP01 image\\pic.png\r\n"
            + "#BMP02 movie\\clip.mp4\r\n"
            + "#STAGEFILE stage.png\r\n"
            + "#BANNER banner.png\r\n"
            + "#BACKBMP missing_back.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            BMSFile legacyFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            BMSFile cacheAwareFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(tempDirectoryPath, Directory.GetFiles(tempDirectoryPath));
            BMSFileMaintenanceInfo legacyInfo = new BMSFileMaintenanceInfo(legacyFile)
            {
                hash = legacyFile.hash
            };
            legacyFile.SetHealthStatus(folderHash, forceUpdate: false, memClear: false, legacyInfo);

            DirectoryResourceLookupCache directoryLookupCache = new DirectoryResourceLookupCache();
            string[] relativeResources =
            {
                "audio\\hit.ogg",
                "image\\pic.jpg",
                "movie\\clip.mp4",
                "stage.jpg",
                "banner.bmp"
            };
            directoryLookupCache.AddDir(tempDirectoryPath, relativeResources);
            DirectoryRelativePathHashIndex relativePathHashIndex = new DirectoryRelativePathHashIndex();
            DirectoryResourceLookupCache.Entry entry = directoryLookupCache.GetEntryOrNull(tempDirectoryPath);
            relativePathHashIndex.AddDir(
                tempDirectoryPath,
                entry.AudioBaseNameHashArray,
                entry.ImageBaseNameHashArray,
                entry.MovieBaseNameHashArray,
                entry.AudioRelativePathHashArray,
                entry.ImageRelativePathHashArray,
                entry.MovieRelativePathHashArray,
                entry.SelfOwnedAudioBaseNameHashArray,
                entry.SelfOwnedImageBaseNameHashArray,
                entry.SelfOwnedMovieBaseNameHashArray,
                entry.SelfOwnedAudioRelativePathHashArray,
                entry.SelfOwnedImageRelativePathHashArray,
                entry.SelfOwnedMovieRelativePathHashArray);
            ResourceHealthLookupContext lookupContext = new ResourceHealthLookupContext(folderHash, directoryLookupCache, relativePathHashIndex);
            BMSFileMaintenanceInfo cacheInfo = new BMSFileMaintenanceInfo(cacheAwareFile)
            {
                hash = cacheAwareFile.hash
            };
            cacheAwareFile.SetHealthStatusUsingLookupContext(lookupContext, forceUpdate: false, memClear: false, cacheInfo);

            Assert.AreEqual(legacyInfo.wav_files_defined, cacheInfo.wav_files_defined);
            Assert.AreEqual(legacyInfo.wav_files_existing, cacheInfo.wav_files_existing);
            Assert.AreEqual(legacyInfo.bga_files_defined, cacheInfo.bga_files_defined);
            Assert.AreEqual(legacyInfo.bga_files_existing, cacheInfo.bga_files_existing);
            Assert.AreEqual(legacyInfo.movie_files_defined, cacheInfo.movie_files_defined);
            Assert.AreEqual(legacyInfo.movie_files_existing, cacheInfo.movie_files_existing);
            Assert.AreEqual(legacyInfo.is_stagefile_existing, cacheInfo.is_stagefile_existing);
            Assert.AreEqual(legacyInfo.is_banner_existing, cacheInfo.is_banner_existing);
            Assert.AreEqual(legacyInfo.is_backbmp_existing, cacheInfo.is_backbmp_existing);
            Assert.IsTrue(lookupContext.CacheHitCount >= 6);
            Assert.AreEqual(0, lookupContext.FileExistsFallbackCount);
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
    public void UpdateMaintenanceInfo_ReportsFallbackKinds()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 sub\\missing.wav\r\n"
            + "#BMP01 image\\missing.png\r\n"
            + "#BMP02 movie\\missing.mp4\r\n"
            + "#STAGEFILE missing_stage.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            BMSFile file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            ResourceHealthLookupContext lookupContext = new ResourceHealthLookupContext(new BMSDirectoryFileNameHash(), null, null);
            BMSFileMaintenanceInfo info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash
            };

            file.SetHealthStatusUsingLookupContext(lookupContext, forceUpdate: false, memClear: false, info);

            Assert.IsTrue(lookupContext.FileExistsFallbackCount >= 4);
            Assert.IsTrue(lookupContext.AudioFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.ImageFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.MovieFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.OptionalImageFileExistsFallbackCount >= 1);
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
    public void UpdateMaintenanceInfo_BmsonParseFailure_DoesNotAbortMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string invalidBmsonPath = Path.Combine(tempDirectoryPath, "invalid.bmson");
        string validBmsonPath = Path.Combine(tempDirectoryPath, "valid.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(invalidBmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
        File.WriteAllText(
            validBmsonPath,
            "{ \"info\": { \"title\": \"Valid\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            PendingChartEntry invalidRow = CreateBmsonRow(invalidBmsonPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            PendingChartEntry validRow = PendingChartEntry.CreateFromBmsonSong(BmsonSongParser.Parse(validBmsonPath));
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                new[] { invalidRow, validRow },
                forceUpdate: true,
                new BMSDirectoryFileNameHash(),
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(2, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            Assert.AreEqual("Valid", validRow.Title);
            Assert.AreEqual(1, validRow.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, validRow.maintenanceInfo.wav_files_existing);

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                List<BMSFileMaintenanceInfo> rows = songDb.Table<BMSFileMaintenanceInfo>().ToList();
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(validRow.path, rows[0].path);
            }
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
    public void CleanupMaintenanceTable_KeepsBmsonRows()
    {
        BmsLibraryMaintenanceService service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        try
        {
            TestableBmsFile bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms.path = Path.Combine(tempDirectoryPath, "chart.bms");
            PendingChartEntry bmson = CreateBmsonRow(Path.Combine(tempDirectoryPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = bms.path, hash = bms.hash }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(BMSFileMaintenanceInfo.CreateForBmson(bmson.path, bmson.hash), typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = Path.Combine(tempDirectoryPath, "stale.bms"), hash = "cccccccccccccccccccccccccccccccc" }, typeof(LR2SongDBExtended.maintenance));
            }

            int deleted = service.CleanupMaintenanceTable(new BMSFile[] { bms, bmson }, new BmsLibraryDbGateway(songDbPath));

            Assert.AreEqual(1, deleted);
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<BMSFileMaintenanceInfo>().Count());
                Assert.IsTrue(songDb.Table<BMSFileMaintenanceInfo>().Any((BMSFileMaintenanceInfo info) => info.path == bms.path));
                Assert.IsTrue(songDb.Table<BMSFileMaintenanceInfo>().Any((BMSFileMaintenanceInfo info) => info.path == bmson.path));
            }
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
        typeof(BMSFile).GetProperty(nameof(BMSFile.notes))!.GetSetMethod(nonPublic: true)!.Invoke(file, new object?[] { value });
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string md5, int notes)
    {
        return new LR2SongDBExtended.chart_info
        {
            md5 = md5,
            sha256 = new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
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
