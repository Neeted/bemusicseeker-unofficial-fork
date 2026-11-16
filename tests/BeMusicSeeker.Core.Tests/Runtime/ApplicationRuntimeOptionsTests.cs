using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Runtime;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationRuntimeOptionsTests
{
    /// <summary>元配列と公開コレクションからの変更を拒み、再起動引数を固定します。5引数構築はargvのログ指定を再解釈せずInfoを保ち、共有ログgetterを変更しません。</summary>
    [TestMethod]
    public void RestartArgumentsRemainCapturedAfterCallerChangesInput()
    {
        (NormalLogLevel LogLevel, bool IsInfoLoggingEnabled, bool HasInvalidLogLevelValue, string InvalidLogLevelValue) processLoggingBefore = (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue);
        string manifest = Path.Combine(Path.GetTempPath(), "検証 session", "session.json");
        string[] arguments = ["--test-session", manifest, "--log-level=Warn"];
        var options = new ApplicationRuntimeOptions(
            ApplicationPathSnapshot.FromExecutablePath(Path.Combine(Path.GetTempPath(), "BeMusicSeeker.exe")),
            new BmsDatabaseLoadOptions(true, true, true),
            new ApplicationTestSession(manifest, "run", "case", "pipe"),
            "https://example.invalid/update.json", arguments);

        arguments[1] = "replacement.json";

        Assert.AreEqual(NormalLogLevel.Info, options.LogOptions.Level);
        Assert.AreEqual(string.Empty, options.LogOptions.InvalidValue);
        CollectionAssert.AreEqual(new[] { "--test-session", manifest, "--log-level=Warn" },
            new List<string>(options.RestartArguments));
        if (options.RestartArguments is IList<string> mutableView)
        {
            Assert.ThrowsExactly<NotSupportedException>(() => mutableView[1] = "replacement.json");
        }
        Assert.AreEqual(processLoggingBefore, (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
    }
}
