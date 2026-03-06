using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

        List<BMSFile> result = service.GetZeroNoteFiles(new BMSFile[] { zeroNoteFile, normalFile });

        CollectionAssert.AreEqual(new[] { zeroNoteFile }, result);
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

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.SetHash(hash);
        return file;
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
    }
}
