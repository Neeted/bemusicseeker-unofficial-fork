using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryZeroNoteRefreshTests
{
    [TestMethod]
    public void RecheckZeroNoteWarnings_RaisesChartFilesZeroNoteWhenWarningsChange()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = "C:\\charts\\normal.bms"
            };
            file.SetWarning(ChartWarningKind.ZeroNoteMismatch, BeMusicSeeker.Properties.Resources.Warning_ZeroNoteMismatch);
            file.SetNotes(1200);
            library.BMSFiles = [file];
            List<string> changedProperties = [];
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                changedProperties.Add(e.PropertyName);
            };

            library.RecheckZeroNoteWarnings();

            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.ChartFilesZeroNote));
            Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
        });
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_DoesNotRaiseChartFilesZeroNoteWhenWarningsDoNotChange()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath), "chart.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n");
            var library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetWarning(ChartWarningKind.ZeroNoteMismatch, BeMusicSeeker.Properties.Resources.Warning_ZeroNoteMismatch);
            file.SetNotes(0);
            file.SetChartInfo(CreateChartInfo(file.hash, notes: 0));
            library.BMSFiles = [file];
            List<string> changedProperties = [];
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                changedProperties.Add(e.PropertyName);
            };

            library.RecheckZeroNoteWarnings();

            CollectionAssert.DoesNotContain(changedProperties, nameof(BMSLibrary.ChartFilesZeroNote));
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(file.HasHighlightedWarning);
            Assert.AreEqual("[1] ゼロノート不整合", file.Warnings.BuildDigestText());
        });
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_SetsStructuredWarningWhenMismatchIsDetected()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath), "chart.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n");
            var library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetNotes(0);
            file.SetChartInfo(CreateChartInfo(file.hash, notes: 0));
            library.BMSFiles = [file];

            library.RecheckZeroNoteWarnings();

            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(file.HasHighlightedWarning);
            Assert.AreEqual("[1] ゼロノート不整合", file.Warnings.BuildDigestText());
        });
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ZeroNoteRefreshTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public TestableBmsFile()
        {
            hash = Guid.NewGuid().ToString("N");
            sha256 = new string('a', 64);
        }

        internal void SetNotes(int? value)
        {
            notes = value;
        }
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

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return MessageBoxResult.OK;
        }
    }
}
