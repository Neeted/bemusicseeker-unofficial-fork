using BeMusicSeeker.TestSupport.Components;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.BMS;
using Ribbit.Logging;
using static BeMusicSeeker.TestSupport.Components.BmsonPlaybackFixture;

namespace BeMusicSeeker.Tests;

/// <summary>再生用bmsonの全曲時計、入力正規化、共有sliceを音源なしで検証します。</summary>
[TestClass]
public sealed class BmsonPlaybackParserTests
{
    /// <summary>
    /// 競合ノートの正常解析だけが除外数付き警告を一回出し、正常な層や解析失敗で誤通知・重複通知する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize] // プロセス共通のNLog設定を一時置換して警告を観測する。
    public Task ConflictingPlayableNotesWarnOnceWithRejectedCountButNormalLayersAndFailedParseDoNot()
    {
        return ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            _ = NLogWrapper.GetLogger(nameof(BmsonPlaybackParser));
            using var warnings = new MemoryTarget { Layout = "${event-properties:item=IgnoredPlayableNotes}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Warn, LogLevel.Warn, warnings);
            LogManager.Configuration = configuration;
            string conflicts = ConflictingPlayableInput("stable-channel-order");
            Assert.AreEqual(8, Parse(conflicts).TotalNoteCount);
            Assert.AreEqual(1, warnings.Logs.Count);
            Assert.AreEqual("6", warnings.Logs[0]);
            warnings.Logs.Clear();
            Assert.AreEqual(3, Parse(LayeredPlayableInput()).TotalNoteCount);
            Assert.AreEqual(0, warnings.Logs.Count);
            InvalidBmsonFileException failure = Assert.ThrowsExactly<InvalidBmsonFileException>(() =>
                Parse(conflicts.Replace("\"init_bpm\":125", "\"init_bpm\":125,\"total\":\"NaN\"", StringComparison.Ordinal)));
            Assert.IsInstanceOfType<FormatException>(failure.InnerException);
            Assert.AreEqual(0, warnings.Logs.Count, "解析を正常完了する前は競合WARNを出しません。");

            return Task.CompletedTask;
        });
    }

}
