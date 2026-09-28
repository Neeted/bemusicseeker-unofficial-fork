using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;

namespace BeMusicSeeker.Tests;

/// <summary>譜面の五群選択、snapshot、安定順、同frame正規化を検証します。</summary>
[TestClass]
public sealed class BmsAudioFrameScheduleTests
{
    [TestMethod]
    public void RequiredIndicesComeOnlyFromTheFiveAudioGroups()
    {
        (TemporaryDirectory directory, BMSFile bms) = CreateChart();
        using TemporaryDirectory directoryCleanup = directory;
        BMSFile.Chart chart = bms.Measures[0];
        chart.Note1PVis01.Clear();
        Add(chart.Bgm, chart, 1, BMSFile.Chart.Note.NoteType.BGM, 0);
        Add(chart.Note1PVis01, chart, 2, BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_01, 0);
        Add(chart.Note2PVis01, chart, 3, BMSFile.Chart.Note.NoteType.NOTE_2P_VISIBLE_01, 0);
        Add(chart.Note1PLng01, chart, 4, BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_01, 0);
        Add(chart.Note2PLng01, chart, 5, BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_01, 0);
        Add(chart.Note1PInv01, chart, 6, BMSFile.Chart.Note.NoteType.NOTE_1P_INVISIBLE_01, 0);
        Add(chart.Note2PBom01, chart, 7, BMSFile.Chart.Note.NoteType.NOTE_2P_BOMB_01, 0);
        Add(chart.Note1PLng02, chart, 8, BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_END_02, 0);

        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4, 5 },
            BmsAudioFrameSchedule.GetRequiredAudioIndices(bms));
        Assert.AreEqual(5, BmsAudioFrameSchedule.Create(bms, 48000).Events.Count);
    }

    [TestMethod]
    public void EventsUseSnapshotsClampingStableGroupOrderAndSameIndexCollisionRules()
    {
        (TemporaryDirectory directory, BMSFile bms) = CreateChart();
        using TemporaryDirectory directoryCleanup = directory;
        BMSFile.Chart chart = bms.Measures[0];
        chart.Note1PVis01.Clear();
        BMSFile.Chart.Note earliest = Add(
            chart.Bgm,
            chart,
            1,
            BMSFile.Chart.Note.NoteType.BGM,
            -20);
        Add(chart.Bgm, chart, 1, BMSFile.Chart.Note.NoteType.BGM, 80);
        Add(chart.Bgm, chart, 2, BMSFile.Chart.Note.NoteType.BGM, 100);
        BMSFile.Chart.Note sameIndexAtFrameOne = Add(
            chart.Note1PVis01,
            chart,
            2,
            BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_01,
            300);
        Add(chart.Note1PLng01, chart, 3, BMSFile.Chart.Note.NoteType.NOTE_1P_LONG_01, 100);
        Add(chart.Note2PLng01, chart, 4, BMSFile.Chart.Note.NoteType.NOTE_2P_LONG_01, 100);
        Add(chart.Note1PVis02, chart, 5, BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_02, 100);
        Add(chart.Note2PVis02, chart, 6, BMSFile.Chart.Note.NoteType.NOTE_2P_VISIBLE_02, 100);
        Add(chart.Note1PVis01, chart, 2, BMSFile.Chart.Note.NoteType.NOTE_1P_VISIBLE_01, 500);

        var schedule = BmsAudioFrameSchedule.Create(bms, 48000);
        BmsAudioFrameEvent[] atFrameZero = schedule.Events
            .Where(item => item.StartFrame == 0)
            .ToArray();

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, atFrameZero.Select(item => item.WavIndex).ToArray());
        Assert.AreEqual(TimeSpan.FromTicks(-20), atFrameZero[0].AbsoluteTime);
        Assert.AreEqual(0L, atFrameZero[0].StartFrame);
        Assert.AreEqual(2L, atFrameZero[1].StableOrder);
        Assert.AreEqual(3L, atFrameZero[2].StableOrder);
        Assert.AreEqual(4L, atFrameZero[3].StableOrder);
        Assert.AreEqual(7L, atFrameZero[4].StableOrder);
        Assert.AreEqual(8L, atFrameZero[5].StableOrder);

        BmsAudioFrameEvent[] wavTwo = schedule.GetEventsForWavIndex(2).ToArray();
        Assert.AreEqual(3, wavTwo.Length);
        Assert.AreEqual(0L, wavTwo[0].StartFrame);
        Assert.AreEqual(1L, wavTwo[0].NextSameIndexStartFrame);
        Assert.AreEqual(1L, wavTwo[1].StartFrame);
        Assert.AreEqual(2L, wavTwo[1].NextSameIndexStartFrame);
        Assert.IsNull(wavTwo[2].NextSameIndexStartFrame);
        Assert.AreEqual(7, schedule.LowerBoundStartFrame(2));
        Assert.AreEqual(schedule.Events.Count, schedule.LowerBoundStartFrame(3));

        earliest.AbsoluteTime = TimeSpan.FromSeconds(3);
        sameIndexAtFrameOne.AbsoluteTime = TimeSpan.FromSeconds(4);
        Assert.AreEqual(TimeSpan.FromTicks(-20), schedule.Events[0].AbsoluteTime);
        Assert.AreEqual(300, schedule.Events.Single(item => item.StableOrder == 5).AbsoluteTime.Ticks);
    }

    private static (TemporaryDirectory Directory, BMSFile Bms) CreateChart()
    {
        var directory = new TemporaryDirectory();
        string path = directory.File("schedule.bms");
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE schedule\n#BPM 120\n#WAV01 dummy.wav\n#00011:01\n",
            Encoding.ASCII);
        return (directory, new BMSFile(path));
    }

    private static BMSFile.Chart.Note Add(
        System.Collections.Generic.ICollection<BMSFile.Chart.Note> collection,
        BMSFile.Chart chart,
        int index,
        BMSFile.Chart.Note.NoteType type,
        long absoluteTicks)
    {
        var note = new BMSFile.Chart.Note(chart, type)
        {
            Index = index,
            AbsoluteTime = TimeSpan.FromTicks(absoluteTicks)
        };
        collection.Add(note);
        return note;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.BmsAudioFrameSchedule." + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => Directory.CreateDirectory(path);

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}
