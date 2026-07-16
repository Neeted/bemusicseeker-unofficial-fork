using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    public void RecheckZeroNoteWarnings_PublishesWarningRefreshWhenWarningsChange()
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
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.RecheckZeroNoteWarnings();

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
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
            library.BMSFiles = [file];
            SeedChartInfoIndex(songDbPath, library, CreateChartInfo(file.hash, notes: 0));
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.RecheckZeroNoteWarnings();

            Assert.AreEqual(0, refreshNotificationChanged);
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(file.Warnings.HasHighlightedWarning);
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
            library.BMSFiles = [file];
            SeedChartInfoIndex(songDbPath, library, CreateChartInfo(file.hash, notes: 0));

            library.RecheckZeroNoteWarnings();

            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(file.Warnings.HasHighlightedWarning);
            Assert.AreEqual("[1] ゼロノート不整合", file.Warnings.BuildDigestText());
        });
    }

    [TestMethod]
    public void ChartFilesZeroNote_UsesOwnedBmsSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, null, new RecordingDialogService());
            var zeroNoteFile = new TestableBmsFile
            {
                path = "C:\\charts\\zero.bms"
            };
            zeroNoteFile.SetSha256(new string('a', 64));
            zeroNoteFile.SetNotes(0);
            var normalFile = new TestableBmsFile
            {
                path = "C:\\charts\\normal.bms"
            };
            normalFile.SetSha256(new string('b', 64));
            normalFile.SetNotes(1000);
            library.BMSFiles = [zeroNoteFile, normalFile];
            library.BmsonSongs =
            [
                new LR2SongDBExtended.bmson_song
                {
                    path = "C:\\charts\\zero.bmson",
                    md5 = Guid.NewGuid().ToString("N")
                }
            ];
            SeedChartInfoIndex(
                songDbPath,
                library,
                CreateChartInfo(zeroNoteFile.hash, notes: 0, sha256: zeroNoteFile.sha256),
                CreateChartInfo(normalFile.hash, notes: 1000, sha256: normalFile.sha256));

            List<ChartFile> result = [.. library.ChartFilesZeroNote];

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(zeroNoteFile, result.Single().GetBmsStorageOwner());
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
            karinotes = value;
        }

        internal void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string md5, int notes, string? sha256 = null)
    {
        return new LR2SongDBExtended.chart_info
        {
            md5 = md5,
            sha256 = sha256 ?? new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
    }

    private static void SeedChartInfoIndex(string songDbPath, BMSLibrary library, params LR2SongDBExtended.chart_info[] chartInfos)
    {
        new BmsLibraryDbGateway(songDbPath).UpsertChartInfos(chartInfos);
        InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);
        Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration did not complete.");
    }

    private static void InvokeDeferredChartInfoHydration(BMSLibrary library, string reason, bool queueFullBackfillAfterHydration)
    {
        MethodInfo method = typeof(BMSLibrary).GetMethod("QueueDeferredChartInfoHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "QueueDeferredChartInfoHydration method was not found.");
        method.Invoke(library, [reason, queueFullBackfillAfterHydration]);
    }

    private static bool WaitForChartInfoHydration(BMSLibrary library)
    {
        return SpinWait.SpinUntil(
            () => library.ChartInfoHydrationRequestedVersion > 0
                && library.ChartInfoHydrationCompletedVersion == library.ChartInfoHydrationRequestedVersion
                && !library.ChartInfoHydrationRunning,
            10000);
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return MessageBoxResult.OK;
        }
    }
}
