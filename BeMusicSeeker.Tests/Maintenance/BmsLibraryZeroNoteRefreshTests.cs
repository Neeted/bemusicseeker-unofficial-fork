using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryZeroNoteRefreshTests
{
    [TestMethod]
    public async Task RecheckZeroNoteWarnings_PublishesWarningRefreshWhenWarningsChange()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());
            ChartFile file = (ChartTestValues.Empty() with { Md5 = new string('a', 32), Token = new OwnedChartToken() }) with
            {
                Path = "C:\\charts\\normal.bms"
            };
            file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, BeMusicSeeker.Properties.Resources.Warning_ZeroNoteMismatch)] };
            file = ChartFileProjection.WithChartInfo(file, new BeMusicSeeker.Models.ChartDetails { md5 = file.Md5, sha256 = file.Sha256, notes = 1200 });
            library.BmsCharts = [file];
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs e)
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
            Assert.IsFalse(library.BmsCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task RecheckZeroNoteWarnings_DoesNotRaiseChartFilesZeroNoteWhenWarningsDoNotChange()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n");
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());
            ChartFile file = (ChartTestValues.Empty() with { Md5 = new string('a', 32), Token = new OwnedChartToken() }) with
            {
                Path = chartPath
            };
            file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, BeMusicSeeker.Properties.Resources.Warning_ZeroNoteMismatch)] };
            file = ChartFileProjection.WithChartInfo(file, new BeMusicSeeker.Models.ChartDetails { md5 = file.Md5, sha256 = file.Sha256, notes = 0 });
            library.BmsCharts = [file];
            await SeedChartInfoIndexAsync(songDbPath, library, CreateChartInfo(file.Md5, notes: 0));
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.RecheckZeroNoteWarnings();

            Assert.AreEqual(0, refreshNotificationChanged);
            Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(file.Warnings));
            Assert.AreEqual("[1] ゼロノート不整合", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
        });
    }

    [TestMethod]
    public async Task RecheckZeroNoteWarnings_SetsStructuredWarningWhenMismatchIsDetected()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n");
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());
            ChartFile file = (ChartTestValues.Empty() with { Md5 = new string('a', 32), Token = new OwnedChartToken() }) with
            {
                Path = chartPath
            };
            file = ChartFileProjection.WithChartInfo(file, new BeMusicSeeker.Models.ChartDetails { md5 = file.Md5, sha256 = file.Sha256, notes = 0 });
            library.BmsCharts = [file];
            await SeedChartInfoIndexAsync(songDbPath, library, CreateChartInfo(file.Md5, notes: 0));

            library.RecheckZeroNoteWarnings();

            ChartFile current = library.BmsCharts.Single();
            Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
            Assert.AreSame(file.Token, current.Token);
            Assert.IsTrue(current.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
            Assert.IsTrue(ChartWarningCollection.HasAnyHighlightedWarning(current.Warnings));
            Assert.AreEqual("[1] ゼロノート不整合", ChartWarningCollection.BuildDigestText(current.Warnings, current.InstallDestination));
        });
    }

    [TestMethod]
    public async Task ChartFilesZeroNote_UsesOwnedBmsSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());
            ChartFile zeroNoteFile = (ChartTestValues.Empty() with { Md5 = new string('a', 32), Token = new OwnedChartToken() }) with
            {
                Path = "C:\\charts\\zero.bms"
            };
            zeroNoteFile = zeroNoteFile with { Sha256 = new string('a', 64) };
            zeroNoteFile = ChartFileProjection.WithChartInfo(zeroNoteFile, new BeMusicSeeker.Models.ChartDetails { md5 = zeroNoteFile.Md5, sha256 = zeroNoteFile.Sha256, notes = 0 });
            ChartFile normalFile = (ChartTestValues.Empty() with { Md5 = new string('a', 32), Token = new OwnedChartToken() }) with
            {
                Path = "C:\\charts\\normal.bms"
            };
            normalFile = normalFile with { Md5 = new string('b', 32), Sha256 = new string('b', 64) };
            normalFile = ChartFileProjection.WithChartInfo(normalFile, new BeMusicSeeker.Models.ChartDetails { md5 = normalFile.Md5, sha256 = normalFile.Sha256, notes = 1000 });
            library.BmsCharts = [zeroNoteFile, normalFile];
            library.BmsonCharts =
            [
                ChartTestValues.Empty(ChartFileKind.Bmson) with {
                    Path = "C:\\charts\\zero.bmson",
                    Md5 = Guid.NewGuid().ToString("N")
                }
            ];
            await SeedChartInfoIndexAsync(
                songDbPath,
                library,
                CreateChartInfo(zeroNoteFile.Md5, notes: 0, sha256: zeroNoteFile.Sha256),
                CreateChartInfo(normalFile.Md5, notes: 1000, sha256: normalFile.Sha256));

            List<ChartFile> result = [.. library.ChartFilesZeroNote];

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(zeroNoteFile.Token, result.Single().Token);
        });
    }

    private static async Task WithTemporarySongDb(Func<string, Task> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ZeroNoteRefreshTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            await testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }


    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string md5, int notes, string? sha256 = null)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            md5 = md5,
            sha256 = sha256 ?? new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
    }

    private static async Task SeedChartInfoIndexAsync(string songDbPath, BMSLibrary library, params BeMusicSeeker.Models.ChartDetails[] chartInfos)
    {
        new BmsLibraryDbGateway(songDbPath).UpsertChartInfos(chartInfos);
        await AwaitChartInfoHydrationAsync(
            library,
            () => InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false));
    }

    private static void InvokeDeferredChartInfoHydration(BMSLibrary library, string reason, bool queueFullBackfillAfterHydration)
    {
        MethodInfo? method = typeof(BMSLibrary).GetMethod("QueueDeferredChartInfoHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "QueueDeferredChartInfoHydration method was not found.");
        method!.Invoke(library, [reason, queueFullBackfillAfterHydration]);
    }

    private static async Task AwaitChartInfoHydrationAsync(BMSLibrary library, Action queueHydration)
    {
        int baselineRequestedVersion = library.ChartInfoHydrationRequestedVersion;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void TryComplete()
        {
            int requestedVersion = library.ChartInfoHydrationRequestedVersion;
            if (requestedVersion <= baselineRequestedVersion
                || library.ChartInfoHydrationCompletedVersion < requestedVersion
                || library.ChartInfoHydrationRunning)
            {
                return;
            }

            completion.TrySetResult(requestedVersion);
        }

        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.ChartInfoHydrationRequestedVersion)
                || args.PropertyName == nameof(BMSLibrary.ChartInfoHydrationCompletedVersion)
                || args.PropertyName == nameof(BMSLibrary.ChartInfoHydrationRunning))
            {
                TryComplete();
            }
        };

        library.PropertyChanged += handler;
        try
        {
            queueHydration();
            TryComplete();
            await completion.Task;
        }
        finally
        {
            library.PropertyChanged -= handler;
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return MessageBoxResult.OK;
        }
    }
}
